// 相機影像解碼器（ES module）：由 camera-scan.js 在按下「相機」後才 import()，條碼槍、PDA 不會載入。
// 選擇順序（同一時間只跑一個解碼器，不會每格同時跑多個）：
// 1. 瀏覽器內建 BarcodeDetector，且同時支援 QR 與一維碼（ean_13、code_128）：Android Chrome。
// 2. 否則載入本機 zxing-wasm reader（/lib/zxing-wasm，固定版本，不走 CDN）：iPhone Safari、Windows Chrome 等，可讀 QR 與一維碼。
// 3. WASM 載入失敗：退回 jsQR，只讀 QR（名稱標示「只讀 QR」，相機狀態列會看到）。
// 內建解碼連續失敗 3 次（宣稱支援但實際不能用）就改用 zxing-wasm。
// 回傳 { text, format }（format 統一成 BarcodeDetector 的名稱，例 ean_13）、{ multiple: true }（畫面上有多個不同的碼）或 null。
// 文字一律字串，不轉數字（保留前導零）；UPC-A／EAN-13 長度差異不在這裡改寫。相容 Chrome 109。

const MAX_DECODE_SIZE = 640;      // jsQR（QR 模組大，640 夠用）
const MAX_DECODE_SIZE_1D = 960;   // zxing-wasm：一維碼線條細，640 時 Code 93、多碼並排會讀不到（tools/DecodeCheck 實測）
const NATIVE_FORMATS = ['qr_code', 'ean_13', 'ean_8', 'upc_a', 'upc_e', 'code_128', 'code_39', 'code_93', 'itf', 'codabar', 'data_matrix'];
const ZXING_FORMATS = ['QRCode', 'EAN13', 'EAN8', 'UPCA', 'UPCE', 'Code128', 'Code39', 'Code93', 'ITF', 'Codabar', 'DataMatrix'];
const ZXING_VERSION = '3.1.6'; // 換版時同步 /lib/zxing-wasm/VERSION.txt
const ZXING_DIR = '/lib/zxing-wasm/';
const NATIVE_FAILURES_BEFORE_SWITCH = 3;

// zxing 的碼制名稱（例 EAN13、Code128、QRCode、ITF14）換成 BarcodeDetector 的名稱
const FORMAT_PREFIXES = [
  ['qrcode', 'qr_code'], ['microqr', 'qr_code'], ['ean13', 'ean_13'], ['ean8', 'ean_8'], ['upca', 'upc_a'], ['upce', 'upc_e'],
  ['code128', 'code_128'], ['code39', 'code_39'], ['code93', 'code_93'], ['itf', 'itf'], ['codabar', 'codabar'], ['datamatrix', 'data_matrix']
];
export function normalizeFormat(name) {
  const key = String(name || '').toLowerCase().replace(/[-_\/ ]/g, '');
  for (let i = 0; i < FORMAT_PREFIXES.length; i++) {
    if (key.indexOf(FORMAT_PREFIXES[i][0]) === 0) return FORMAT_PREFIXES[i][1];
  }
  return key || null;
}

// 多個結果：內容不同才算「多個碼」（同一個碼被讀到兩次不算）
function pickOne(results) {
  if (!results.length) return null;
  const texts = results.map(function (r) { return r.text; }).filter(function (t, i, all) { return all.indexOf(t) === i; });
  return texts.length > 1 ? { multiple: true } : results[0];
}

function loadScript(src, ready) {
  return new Promise(function (resolve, reject) {
    if (ready()) { resolve(); return; }
    const script = document.createElement('script');
    script.src = src;
    script.onload = function () { resolve(); };
    script.onerror = function () { reject(new Error('無法載入 ' + src)); };
    document.head.appendChild(script);
  });
}

// 影格縮小（jsQR 640 px、WASM 960 px）再解碼，降低舊手機 CPU 與耗電
function frameReader(maxSize) {
  const canvas = document.createElement('canvas');
  const context = canvas.getContext('2d', { willReadFrequently: true });
  return function (source) {
    const width = source.videoWidth || source.naturalWidth || source.width;
    const height = source.videoHeight || source.naturalHeight || source.height;
    const scale = Math.min(1, maxSize / Math.max(width, height));
    canvas.width = Math.round(width * scale);
    canvas.height = Math.round(height * scale);
    context.drawImage(source, 0, 0, canvas.width, canvas.height);
    return context.getImageData(0, 0, canvas.width, canvas.height);
  };
}

async function nativeDecoder() {
  if (!('BarcodeDetector' in window)) return null;
  try {
    const supported = await window.BarcodeDetector.getSupportedFormats();
    const formats = NATIVE_FORMATS.filter(function (f) { return supported.indexOf(f) >= 0; });
    // 只支援 QR（或沒有主要一維碼）的內建解碼器不夠用，改用 WASM
    if (formats.indexOf('qr_code') < 0 || formats.indexOf('ean_13') < 0 || formats.indexOf('code_128') < 0) return null;
    const detector = new window.BarcodeDetector({ formats: formats });
    return {
      name: 'BarcodeDetector（瀏覽器內建）',
      qrOnly: false,
      decode: async function (source) {
        const codes = await detector.detect(source);
        return pickOne(codes.map(function (c) { return { text: c.rawValue, format: c.format }; }));
      }
    };
  } catch (e) {
    return null; // 宣稱支援但無法建立：改用 WASM
  }
}

async function zxingDecoder() {
  await loadScript(ZXING_DIR + 'zxing-reader.js?v=' + ZXING_VERSION, function () { return !!window.ZXingWASM; });
  const zx = window.ZXingWASM;
  // wasm 從本機 /lib/zxing-wasm/ 取得（套件預設是 jsDelivr CDN）；?v= 讓 Service Worker 以版本快取
  await zx.prepareZXingModule({
    overrides: {
      locateFile: function (path, prefix) {
        return path.endsWith('.wasm') ? ZXING_DIR + path + '?v=' + ZXING_VERSION : prefix + path;
      }
    },
    fireImmediately: true
  });
  const read = frameReader(MAX_DECODE_SIZE_1D);
  const options = { formats: ZXING_FORMATS, tryHarder: true, maxNumberOfSymbols: 2 };
  return {
    name: 'zxing-wasm ' + ZXING_VERSION + '（QR 與一維碼）',
    qrOnly: false,
    decode: async function (source) {
      const results = await zx.readBarcodes(read(source), options);
      return pickOne(results.filter(function (r) { return r.isValid && r.text; })
        .map(function (r) { return { text: r.text, format: normalizeFormat(r.format) }; }));
    }
  };
}

async function jsqrDecoder() {
  await loadScript('/lib/jsqr/jsQR.js', function () { return !!window.jsQR; });
  const read = frameReader(MAX_DECODE_SIZE);
  return {
    name: 'jsQR（只讀 QR，一維條碼請改用條碼槍或手動輸入）',
    qrOnly: true,
    decode: async function (source) {
      const image = read(source);
      const code = window.jsQR(image.data, image.width, image.height, { inversionAttempts: 'dontInvert' });
      return code ? { text: code.data, format: 'qr_code' } : null;
    }
  };
}

async function fallbackDecoder() {
  try {
    return await zxingDecoder();
  } catch (e) {
    return await jsqrDecoder();
  }
}

// options.prefer：'zxing' 強制用 WASM（自動化實測用）
export async function createDecoder(options) {
  const prefer = options && options.prefer;
  let current = prefer === 'zxing' ? await zxingDecoder() : (await nativeDecoder()) || await fallbackDecoder();
  let failures = 0;
  let switching = null;
  const wrapper = {
    get name() { return current.name; },
    get qrOnly() { return current.qrOnly; },
    decode: async function (source) {
      if (switching) return null;
      try {
        const result = await current.decode(source);
        failures = 0;
        return result;
      } catch (e) {
        failures++;
        if (current.name.indexOf('BarcodeDetector') === 0 && failures >= NATIVE_FAILURES_BEFORE_SWITCH) {
          switching = fallbackDecoder().then(function (next) {
            current = next;
            if (options && options.onSwitch) options.onSwitch(next.name);
          }).catch(function () { /* WASM 與 jsQR 都載不到：繼續用內建解碼器 */ }).finally(function () { switching = null; failures = 0; });
        }
        throw e;
      }
    }
  };
  return wrapper;
}
