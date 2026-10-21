// 相機掃碼模組（ES module）：只在使用者按下「開始相機掃碼」時以 import() 載入。
// 1. 以 getUserMedia 開啟後鏡頭（需要 HTTPS 安全內容）。
// 2. 解碼器：瀏覽器內建 BarcodeDetector 支援 qr_code 時使用（Android Chrome）；
//    否則動態載入 /lib/jsqr/jsQR.js（iPhone Safari、Windows Chrome）。
// 3. 每 200 ms 解碼一次並縮小畫面到最長 640 px，降低舊手機 CPU 與耗電。
// 4. 讀到 QR、按停止、切到背景或離開頁面時，立即停止所有 track 釋放相機。
// 相容 Chrome 109：只用原生 API。

const DECODE_INTERVAL_MS = 200;
const MAX_DECODE_SIZE = 640;

let stream = null;
let timer = null;
let decoder = null;
let callbacks = null;
let startedAt = 0;

function setState(text) {
  if (callbacks) callbacks.onState(text);
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
      const formats = await window.BarcodeDetector.getSupportedFormats();
      if (formats.indexOf('qr_code') >= 0) {
        const detector = new window.BarcodeDetector({ formats: ['qr_code'] });
        return {
          name: 'BarcodeDetector（瀏覽器內建）',
          decode: async function (video) {
            const codes = await detector.detect(video);
            return codes.length ? codes[0].rawValue : null;
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
    name: 'jsQR',
    decode: async function (video) {
      const scale = Math.min(1, MAX_DECODE_SIZE / Math.max(video.videoWidth, video.videoHeight));
      canvas.width = Math.round(video.videoWidth * scale);
      canvas.height = Math.round(video.videoHeight * scale);
      context.drawImage(video, 0, 0, canvas.width, canvas.height);
      const image = context.getImageData(0, 0, canvas.width, canvas.height);
      const code = window.jsQR(image.data, image.width, image.height, { inversionAttempts: 'dontInvert' });
      return code ? code.data : null;
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
  try {
    if (video.readyState >= 2) {
      const text = await decoder.decode(video);
      if (text) {
        const elapsed = Math.round(performance.now() - startedAt);
        stop();
        callbacks.onResult(text, elapsed);
        return;
      }
    }
  } catch (e) {
    // 單一畫面解碼失敗不中斷，下一輪再試
  }
  timer = setTimeout(function () { tick(video); }, DECODE_INTERVAL_MS);
}

export async function start(video, handlers) {
  callbacks = handlers;
  if (stream) return;
  if (!window.isSecureContext || !navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
    handlers.onError(describeError({ name: 'SecurityError' }));
    return;
  }
  setState('啟動中');
  try {
    decoder = decoder || await pickDecoder();
    handlers.onDecoder(decoder.name);
    stream = await navigator.mediaDevices.getUserMedia({
      video: { facingMode: { ideal: 'environment' }, width: { ideal: 1280 } },
      audio: false
    });
    video.srcObject = stream;
    await video.play();
    startedAt = performance.now();
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
  const video = document.getElementById('camera-video');
  if (video) video.srcObject = null;
  setState('停止');
}

// 切到背景或離開頁面時釋放相機
document.addEventListener('visibilitychange', function () {
  if (document.visibilityState === 'hidden') stop();
});
window.addEventListener('pagehide', stop);
