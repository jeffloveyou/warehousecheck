// 相機掃碼模組（ES module）：只在使用者按下「相機掃碼」時以 import() 載入（T-PERF-05：條碼槍、PDA 不會載入）。
// 1. 以 getUserMedia 開啟後鏡頭（需要 HTTPS 安全內容）。影像只在這台裝置解碼，不上傳。
// 2. 解碼器在 camera-decoder.js：內建 BarcodeDetector（含一維碼）→ 本機 zxing-wasm（QR＋一維碼）→ jsQR（只讀 QR）。
// 3. 每 200 ms 解碼一次（影格縮小後才解碼），降低舊手機 CPU 與耗電。
// 4. continuous（連續掃碼面板、查料）：讀到後回報、暫停 1.5 秒再繼續；同一個碼要先離開畫面 1.5 秒才會再回報（偶爾漏讀幾格不算離開）；
//    否則（單欄相機、/dev/camera）讀到一次就停止。畫面上同時有多個不同的碼：不回報，提示只對準一個。
// 5. 同一頁只有一個相機工作階段：開新的會先停掉舊的（呼叫舊的 onStop 讓它收起畫面）。
//    每次開始／停止都換一個代號；權限詢問或初始化中按了停止，之後回來的相機會立刻關掉，延遲的解碼結果也會丟棄。
// 6. 按停止、切到背景或離開頁面時，立即停止所有 track 釋放相機。
// handlers：onResult(text, ms, format)、onError(message)、onState(text)、onDecoder(name)、onStop()（都可省略，onResult 除外）
// 相容 Chrome 109：只用原生 API。

const DECODE_INTERVAL_MS = 200;
const PAUSE_AFTER_RESULT_MS = 1500;
// 同一個碼要先離開畫面這麼久（期間沒再被讀到）才會再回報：鏡頭停在同一個條碼上不會重複計數（PR 2 審查 M2）
const SAME_CODE_GONE_MS = 1500;

let session = 0;
let stream = null;
let timer = null;
let decoder = null;
let decoderLoading = null;
let callbacks = null;
let currentVideo = null;
let continuous = false;
let startedAt = 0;
let last = { text: null, seenAt: 0 };
let multipleShown = false;

function setState(handlers, text) {
  if (handlers && handlers.onState) handlers.onState(text);
  document.body.dataset.cameraState = text;
}

// 解碼器模組網址帶檔案版本（_Layout 的 meta），換版後不會用到瀏覽器快取裡的舊檔
function decoderUrl() {
  const meta = document.querySelector('meta[name="wh-camera-decoder-v"]');
  return '/js/camera-decoder.js' + (meta && meta.content ? '?v=' + encodeURIComponent(meta.content) : '');
}

function loadDecoder(handlers) {
  if (decoder) return Promise.resolve(decoder);
  if (!decoderLoading) {
    decoderLoading = import(decoderUrl()).then(function (module) {
      return module.createDecoder({
        onSwitch: function (name) { if (callbacks && callbacks.onDecoder) callbacks.onDecoder(name); }
      });
    }).then(function (d) {
      decoder = d;
      return d;
    }).finally(function () { decoderLoading = null; });
  }
  return decoderLoading;
}

function describeError(error) {
  switch (error && error.name) {
    case 'NotAllowedError':
      return '已拒絕相機權限。iPhone：設定 → Safari → 相機 → 允許；Android：點網址列左側圖示 → 權限 → 相機 → 允許，然後重新整理。';
    case 'NotFoundError':
    case 'OverconstrainedError':
      return '找不到可用的相機。';
    case 'NotReadableError':
    case 'AbortError':
      return '相機正被其他程式使用，請關閉其他使用相機的 App 後再試。';
    case 'SecurityError':
      return '目前不是安全連線（HTTPS），瀏覽器不允許使用相機。';
    default:
      return '相機啟動失敗：' + (error && error.message ? error.message : error);
  }
}

// 這台裝置能不能開相機；不能時回傳原因（給按鈕顯示），可以時回傳 null
export function unsupportedReason() {
  if (!window.isSecureContext) return describeError({ name: 'SecurityError' });
  if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) return '這個瀏覽器不支援相機，請改用條碼槍或手動輸入。';
  return null;
}

async function tick(video, mine) {
  if (mine !== session) return;
  let wait = DECODE_INTERVAL_MS;
  try {
    if (video.readyState >= 2) {
      const result = await decoder.decode(video);
      if (mine !== session) return; // 解碼期間已停止或換了工作階段：結果丟棄
      const now = performance.now();
      if (result && result.multiple) {
        if (!multipleShown) setState(callbacks, '畫面有多個條碼，請只對準一個');
        multipleShown = true;
      } else {
        if (multipleShown) setState(callbacks, '掃描中');
        multipleShown = false;
        if (result && result.text && result.text === last.text && now - last.seenAt < SAME_CODE_GONE_MS) {
          last.seenAt = now; // 還在畫面上：不回報，延長「還看得到」的時間
        } else if (result && result.text) {
          last = { text: result.text, seenAt: now + (continuous ? PAUSE_AFTER_RESULT_MS : 0) };
          const elapsed = Math.round(now - startedAt);
          const handlers = callbacks;
          if (!continuous) {
            stop();
            handlers.onResult(result.text, elapsed, result.format);
            return;
          }
          handlers.onResult(result.text, elapsed, result.format);
          wait = PAUSE_AFTER_RESULT_MS;
        }
      }
    }
  } catch (e) {
    // 單一畫面解碼失敗不中斷，下一輪再試（內建解碼器連續失敗會自動改用 WASM）
  }
  if (mine === session && stream) {
    timer = setTimeout(function () { tick(video, mine); }, wait);
  }
}

// options.continuous：連續掃碼（讀到後繼續），預設讀到一次就停止
export async function start(video, handlers, options) {
  stop(); // 同一頁只留一個相機：先停掉舊的（舊的 onStop 會收起它的畫面）
  const mine = session;
  callbacks = handlers;
  continuous = !!(options && options.continuous);
  const reason = unsupportedReason();
  if (reason) {
    callbacks = null;
    if (handlers.onError) handlers.onError(reason);
    return;
  }
  setState(handlers, '啟動中');
  try {
    const d = await loadDecoder(handlers);
    if (mine !== session) return;
    if (handlers.onDecoder) handlers.onDecoder(d.name);
    const media = await navigator.mediaDevices.getUserMedia({
      video: { facingMode: { ideal: 'environment' }, width: { ideal: 1280 } },
      audio: false
    });
    if (mine !== session) {
      // 等權限期間已按停止或切到別的欄位：相機不再開啟
      media.getTracks().forEach(function (track) { track.stop(); });
      return;
    }
    stream = media;
    currentVideo = video;
    video.srcObject = media;
    await video.play();
    if (mine !== session) return;
    startedAt = performance.now();
    last = { text: null, seenAt: 0 };
    multipleShown = false;
    setState(handlers, d.qrOnly ? '掃描中（只讀 QR）' : '掃描中');
    tick(video, mine);
  } catch (error) {
    if (mine !== session) return;
    stop();
    if (handlers.onError) handlers.onError(describeError(error));
  }
}

export function stop() {
  session++;
  if (timer) { clearTimeout(timer); timer = null; }
  if (stream) {
    stream.getTracks().forEach(function (track) { track.stop(); });
    document.body.dataset.trackState = stream.getTracks().map(function (t) { return t.readyState; }).join(',');
    stream = null;
  }
  const video = currentVideo || document.getElementById('camera-video');
  if (video) video.srcObject = null;
  currentVideo = null;
  const handlers = callbacks;
  callbacks = null;
  setState(handlers, '停止');
  if (handlers && handlers.onStop) handlers.onStop();
}

export function isRunning() {
  return stream !== null;
}

// 切到背景或離開頁面時釋放相機
document.addEventListener('visibilitychange', function () {
  if (document.visibilityState === 'hidden') stop();
});
window.addEventListener('pagehide', stop);
