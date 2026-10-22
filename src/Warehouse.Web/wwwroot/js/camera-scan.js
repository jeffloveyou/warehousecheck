// 相機掃碼模組（ES module）：只在使用者按下「相機掃碼」時以 import() 載入（T-PERF-05：條碼槍、PDA 不會載入）。
// 1. 以 getUserMedia 開啟後鏡頭（需要 HTTPS 安全內容）。
// 2. 解碼器：瀏覽器內建 BarcodeDetector 時使用（Android Chrome），可讀 QR 與常見一維碼；
//    否則動態載入 /lib/jsqr/jsQR.js（iPhone Safari、Windows Chrome），只讀 QR。
// 3. 每 200 ms 解碼一次並縮小畫面到最長 640 px，降低舊手機 CPU 與耗電。
// 4. continuous（連續掃碼面板）：讀到後回報、暫停 1.5 秒再繼續；同一個碼要先離開畫面 1.5 秒才會再回報（偶爾漏讀幾格不算離開）；
//    否則（/dev/camera）讀到一次就停止。
// 5. 按停止、切到背景或離開頁面時，立即停止所有 track 釋放相機。
// 相容 Chrome 109：只用原生 API。

const DECODE_INTERVAL_MS = 200;
const MAX_DECODE_SIZE = 640;
const PAUSE_AFTER_RESULT_MS = 1500;
// 同一個碼要先離開畫面這麼久（期間沒再被讀到）才會再回報：鏡頭停在同一個條碼上不會重複計數（PR 2 審查 M2）
const SAME_CODE_GONE_MS = 1500;
const WANTED_FORMATS = ['qr_code', 'ean_13', 'ean_8', 'upc_a', 'upc_e', 'code_128', 'code_39', 'itf', 'codabar', 'data_matrix'];

let stream = null;
let timer = null;
let decoder = null;
let callbacks = null;
let currentVideo = null;
let continuous = false;
let startedAt = 0;
let last = { text: null, seenAt: 0 };

function setState(text) {
  if (callbacks && callbacks.onState) callbacks.onState(text);
  document.body.dataset.cameraState = text;
}

function loadScript(src) {
  return new Promise(function (resolve, reject) {
    if (window.jsQR) { resolve(); return; }
    const script = document.createElement('script');
    script.src = src;
    script.onload = function () { resolve(); };
    script.onerror = function () { reject(new Error('無法載入 ' + src)); };
    document.head.appendChild(script);
  });
}

async function pickDecoder() {
  if ('BarcodeDetector' in window) {
    try {
      const supported = await window.BarcodeDetector.getSupportedFormats();
      const formats = WANTED_FORMATS.filter(function (f) { return supported.indexOf(f) >= 0; });
      if (formats.indexOf('qr_code') >= 0) {
        const detector = new window.BarcodeDetector({ formats: formats });
        return {
          name: 'BarcodeDetector（瀏覽器內建：' + formats.join('、') + '）',
          decode: async function (video) {
            const codes = await detector.detect(video);
            return codes.length ? { text: codes[0].rawValue, format: codes[0].format } : null;
          }
        };
      }
    } catch (e) {
      // 支援宣告但無法使用時改用 jsQR
    }
  }

  await loadScript('/lib/jsqr/jsQR.js');
  const canvas = document.createElement('canvas');
  const context = canvas.getContext('2d', { willReadFrequently: true });
  return {
    name: 'jsQR（只讀 QR Code）',
    decode: async function (video) {
      const scale = Math.min(1, MAX_DECODE_SIZE / Math.max(video.videoWidth, video.videoHeight));
      canvas.width = Math.round(video.videoWidth * scale);
      canvas.height = Math.round(video.videoHeight * scale);
      context.drawImage(video, 0, 0, canvas.width, canvas.height);
      const image = context.getImageData(0, 0, canvas.width, canvas.height);
      const code = window.jsQR(image.data, image.width, image.height, { inversionAttempts: 'dontInvert' });
      return code ? { text: code.data, format: 'qr_code' } : null;
    }
  };
}

function describeError(error) {
  switch (error && error.name) {
    case 'NotAllowedError':
      return '已拒絕相機權限。iPhone：設定 → Safari → 相機 → 允許；Android：點網址列左側圖示 → 權限 → 相機 → 允許，然後重新整理。';
    case 'NotFoundError':
    case 'OverconstrainedError':
      return '找不到可用的相機。';
    case 'NotReadableError':
      return '相機正被其他程式使用，請關閉其他使用相機的 App 後再試。';
    case 'SecurityError':
      return '目前不是安全連線（HTTPS），瀏覽器不允許使用相機。';
    default:
      return '相機啟動失敗：' + (error && error.message ? error.message : error);
  }
}

async function tick(video) {
  if (!stream) return;
  let wait = DECODE_INTERVAL_MS;
  try {
    if (video.readyState >= 2) {
      const result = await decoder.decode(video);
      const now = performance.now();
      if (result && result.text && result.text === last.text && now - last.seenAt < SAME_CODE_GONE_MS) {
        last.seenAt = now; // 還在畫面上：不回報，延長「還看得到」的時間
      } else if (result && result.text) {
        last = { text: result.text, seenAt: now + (continuous ? PAUSE_AFTER_RESULT_MS : 0) };
        const elapsed = Math.round(now - startedAt);
        if (!continuous) {
          stop();
          callbacks.onResult(result.text, elapsed, result.format);
          return;
        }
        callbacks.onResult(result.text, elapsed, result.format);
        wait = PAUSE_AFTER_RESULT_MS;
      }
    }
  } catch (e) {
    // 單一畫面解碼失敗不中斷，下一輪再試
  }
  if (stream) {
    timer = setTimeout(function () { tick(video); }, wait);
  }
}

// options.continuous：連續掃碼（讀到後繼續），預設讀到一次就停止
export async function start(video, handlers, options) {
  callbacks = handlers;
  continuous = !!(options && options.continuous);
  if (stream) return;
  if (!window.isSecureContext || !navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
    handlers.onError(describeError({ name: 'SecurityError' }));
    return;
  }
  setState('啟動中');
  try {
    decoder = decoder || await pickDecoder();
    if (handlers.onDecoder) handlers.onDecoder(decoder.name);
    stream = await navigator.mediaDevices.getUserMedia({
      video: { facingMode: { ideal: 'environment' }, width: { ideal: 1280 } },
      audio: false
    });
    currentVideo = video;
    video.srcObject = stream;
    await video.play();
    startedAt = performance.now();
    last = { text: null, seenAt: 0 };
    setState('掃描中');
    tick(video);
  } catch (error) {
    stop();
    handlers.onError(describeError(error));
  }
}

export function stop() {
  if (timer) { clearTimeout(timer); timer = null; }
  if (stream) {
    stream.getTracks().forEach(function (track) { track.stop(); });
    document.body.dataset.trackState = stream.getTracks().map(function (t) { return t.readyState; }).join(',');
    stream = null;
  }
  const video = currentVideo || document.getElementById('camera-video');
  if (video) video.srcObject = null;
  currentVideo = null;
  setState('停止');
}

export function isRunning() {
  return stream !== null;
}

// 切到背景或離開頁面時釋放相機
document.addEventListener('visibilitychange', function () {
  if (document.visibilityState === 'hidden') stop();
});
window.addEventListener('pagehide', stop);
