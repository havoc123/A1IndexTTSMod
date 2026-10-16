namespace A1IndexTTSMod
{
    /// <summary>
    /// 手机端页面（单文件，内嵌HTML/CSS/JS）。
    /// 手机浏览器打开 http://&lt;电脑IP&gt;:8895/phone 即是 NPC 专用窗口。
    ///
    /// 设计要点：
    ///   - MJPEG 用 &lt;img src="/mjpeg"&gt;，浏览器原生支持 multipart/x-mixed-replace，
    ///     **无需任何前端库、无需 WebRTC** —— 这正是换方案换来的最大好处。
    ///   - 局域网 http 直接可跑（只有摄像头才需要 https）。
    ///   - 竖屏优先，因为是"手机当窗口用"。
    ///   - 断线自动重连：img onerror 触发重连。
    /// </summary>
    internal static class PhonePage
    {
        public const string Html = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1, viewport-fit=cover"">
<title>NPC 通话</title>
<style>
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  html, body {
    margin: 0; padding: 0; height: 100%; background: #0b0b0d; color: #eee;
    font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, sans-serif;
    overflow: hidden; user-select: none;
  }
  #stage { position: fixed; inset: 0; display: flex; flex-direction: column; }
  #view {
    flex: 1 1 auto; background: #000; overflow: hidden; position: relative;
  }
  /* ★★画面定位改为**由 JS 绝对定位**（left/top/width/height，单位 px），
     不再用 object-fit。
     为什么必须换掉 object-fit —— 这是本轮的核心重构：
       ① object-fit 的绘制区域要靠 `scale = cover ? max(...) : min(...)` 反算，
          任何「额外缩放」一叠加（捏合放大 + 拖动平移），这套反算立刻失效，
          点偏是必然的（用户报的就是点不准、看不全）。
       ② 改成 JS 直接摆位后，img.getBoundingClientRect() **就是屏幕上真实那块画面**，
          归一化坐标退化成两行除法 `(clientX-rect.left)/rect.width`，
          与 contain/cover、缩放、平移全部无关 —— 永远不会点偏。
     transform-origin:0 0 是为了缩放锚点计算干净。 */
  #view img {
    position: absolute; left: 0; top: 0; display: block;
    transform-origin: 0 0; -webkit-user-drag: none;
  }
  /* 提示气泡（2026-10-15 移位，用户实测要求）：原先是 #view（画面内）的
     absolute bottom:10% 子元素 —— 画面缩小到和 NPC 对话差不多大小时，
     气泡正好压住游戏内的聊天框。现移到 #bar 里、用 bottom:100% 贴在
     输入栏正上方，永远固定在屏幕底缘，不再挡游戏 UI。
     ★max-width + white-space:normal：最长消息（「声音通道未启动…」30+ 字）
       原来 nowrap 会溢出屏幕、左侧被裁看不全，改为允许换行。 */
  #hint {
    position: absolute; left: 50%; bottom: 100%;
    transform: translateX(-50%);
    margin-bottom: 6px;
    background: rgba(0,0,0,.72); color: #ddd; font-size: 12px;
    padding: 7px 12px; border-radius: 8px; pointer-events: none;
    display: none; white-space: normal; text-align: center;
    max-width: 92vw;
  }
  /* ★底部操作栏（2026-10-14 大精简，按用户明确要求）：
     原来那一排 看看/聊聊/再见/挂断/诊断/画面/隐藏面板 六个按钮，
     加上「♪ 声音」行和「帧 N · 分辨率 · fps」状态行，全部撤掉 —— 太占屏幕。
     现在只留**输入框 + 发送**，常驻显示，不再有「隐藏/唤出」这套机制
     （PC 空格键唤出面板的方案随之作废，ControlServer 的 /panel 端点保留但不使用）。 */
  #bar {
    flex: 0 0 auto; padding: 10px 12px calc(10px + env(safe-area-inset-bottom));
    background: #16161a; border-top: 1px solid #2a2a30;
    display: flex; gap: 8px; align-items: center;
    position: relative;   /* #hint 的定位锚点（贴在 #bar 上方） */
  }
  /* 打字区：手机输入框 → POST /type → 直写游戏对话输入框。
     独占一整行（flex:1），竖屏横屏都是完整宽度，中文输入法不会被压扁。 */
  #typerow { flex: 1 1 auto; display: flex; gap: 8px; }
  #tin {
    flex: 1 1 auto; min-width: 0;   /* ★min-width:0 否则 flex 子项不肯收缩 */
    padding: 11px 12px; border-radius: 10px;
    border: 1px solid #33333c; background: #101014; color: #eee;
    font-size: 15px;                /* ★≥16px 才不会触发 iOS 自动放大页面 */
    outline: none;
  }
  #tin:focus { border-color: #4a9eff; }
  #tin::placeholder { color: #66666f; }
  /* ★body 是 user-select:none（防止点画面时选中文字），必须对输入框单独放开，
     否则手机上无法长按选字/粘贴，中文输入体验直接崩掉。 */
  #tin, #tin * { user-select: text; -webkit-user-select: text; }
  .btn {
    flex: 0 0 auto; padding: 11px 18px; border-radius: 10px; border: none;
    font-size: 15px; font-weight: 500; color: #fff; background: #3a3a44;
    min-width: 62px; text-align: center;
  }
  .btn:active { background: #4a4a56; }
  .btn.send { background: #2f7d4f; }
  .btn.send:active { background: #256640; }
  /* 麦克风按钮：录音中变红 + 按钮上直接显示秒数（不用额外状态行，用户要求过） */
  .btn.mic { background: #3a3a44; }
  .btn.mic.rec { background: #b03030; }
  .btn.mic.rec:active { background: #8c2626; }
  #overlay {
    position: absolute; inset: 0; display: none; align-items: center; justify-content: center;
    background: rgba(0,0,0,.82); color: #bbb; font-size: 14px; text-align: center; padding: 24px;
  }
</style>
</head>
<body>
<div id=""stage"">
  <div id=""view"">
    <img id=""cam"" alt=""video"">
    <div id=""overlay"">连接中…</div>
  </div>
  <!-- ★底部只有「输入框 + 发送」，常驻显示（用户 2026-10-14 明确要求）。
       原来这里还有 看看/聊聊/再见/挂断/诊断/画面/隐藏面板 六个按钮、
       ♪声音行、帧状态行，全部撤掉 —— 占屏幕且大部分用不上。
       游戏里的「看看/聊聊/再见/挂断」可以直接点画面透传，不需要额外按钮。 -->
  <div id=""bar"">
    <!-- ★提示气泡（2026-10-15 用户要求移位）：从 #view（画面内）移到这里，
         贴在输入栏正上方。原来 absolute bottom:10% 相对画面定位，
         画面缩小到和 NPC 对话差不多大小时，气泡正好压住游戏内的聊天框；
         移到 #bar 上方后永远固定在屏幕底缘，不再挡游戏 UI。 -->
    <div id=""hint""></div>
    <div id=""typerow"">
      <input id=""tin"" type=""text"" placeholder=""对 NPC 说点什么…"" autocomplete=""off""
             enterkeyhint=""send"" maxlength=""200"">
      <!-- ★麦克风按钮（2026-10-14）：与发送按钮并列。它是**游戏内麦克风按钮的
           远程映射**——点一下 = 游戏内点麦克风（PC 麦克风 + MOD 本地 ASR），
           再点一下停止；识别文本自动上屏并发送（与游戏内行为一致）。
           手机不录音、不需要麦克风权限，http 局域网源也能用（含 iPhone）。 -->
      <button class=""btn mic"" id=""mic"">语音</button>
      <button class=""btn send"" onclick=""sendType(true)"">发送</button>
    </div>
  </div>
</div>

<script>
(function () {
  var img = document.getElementById('cam');
  var overlay = document.getElementById('overlay');
  var retry = 1000;

  // ★★ 控制通道（打字/点击/按钮/状态）走独立端口 8894，画面与声音留在 8895。
  //
  // 【为什么必须换端口，而不是继续优化 sendBeacon —— 上一版踩过】
  //   用户实测：改成 sendBeacon 后**依然**是「关掉网页才生效」。
  //   原因：sendBeacon **不是独立通道**，它同样受同源 6 条连接池限制，只是优先级更低。
  //   槽位占满时 beacon 会被压进「页面卸载时才 flush」的队列 —— 服务端日志能看见
  //   请求最终到达，所以极易误判成「后端慢」（本次就是这样绕了一圈）。
  //   ⇒ 真正的解法是**换 origin**：Chrome 的连接池按 (协议, 主机, 端口) 分组，
  //     8894 有自己完整的 6 条槽位，8895 上的 MJPEG/audio 长流再忙也挤不占。
  //   附带收益：8894 全是短请求，可以**用 fetch 并立刻读到响应**，
  //     于是能马上回显「游戏那边收到了没」，不必再靠轮询对账。
  // ★定义必须放在使用点之前（点击透传在下面），否则 var 提升会拿到 undefined。
  var CTRL = 'http://' + location.hostname + ':8894';

  function showOverlay(text) {
    overlay.textContent = text;
    overlay.style.display = 'flex';
  }
  function hideOverlay() { overlay.style.display = 'none'; }

  // MJPEG 流：浏览器原生支持 multipart/x-mixed-replace
  // ==================================================================
  //  ★★★ 画面布局与手势（本轮重构的核心，取代原来整套 object-fit 反算）
  //
  // 【为什么推翻 object-fit 方案】
  //   原方案靠 `scale = cover ? max(cw/iw,ch/ih) : min(cw/iw,ch/ih)` 反算图片绘制区域。
  //   只要叠加「用户缩放倍率」和「拖动平移」两层额外变换，这套反算立刻失效 ——
  //   实测症状就是点不准、看不全。属于**结构性缺陷，不是参数没调好**。
  //   ⇒ 改成：JS 自己算出图片的最终 left/top/width/height 直接写样式。
  //     这样 img.getBoundingClientRect() 返回的就是**屏幕上真实那一块画面**，
  //     归一化坐标退化成两行除法，与缩放/平移/方向全部解耦 → 永远不会点偏。
  //
  // 【zoom 的定义】以「contain（整幅完整可见）」为 1.0
  //   zoom = 1.0  完整画面（含下方对话选项框）
  //   zoom > 1.0  放大凑近看 NPC，两侧/上下被裁掉
  //   平移 panX/panY 在 layout() 里夹紧，保证画面边缘不会缩进视口内留黑缝。
  //
  // 【手势分工】三者互不打架：
  //   单击          → 透传成游戏里的真实点击
  //   单指拖动      → 平移（超过 10px 判为拖动，就不再当点击）
  //   双指捏合      → 缩放（锚点=两指中点，所指的那块画面不动）
  //   长按 0.6 秒   → 吞掉这次点击，不误触游戏（★面板撤掉后的新用途）
  // ==================================================================
  var view = document.getElementById('view');
  var hintEl = document.getElementById('hint');
  var ZOOM_MIN = 1, ZOOM_MAX = 5;
  var zoom = 1, panX = 0, panY = 0;
  var tapCount = 0;

  function naturalSize() {
    return { w: img.naturalWidth || 640, h: img.naturalHeight || 480 };
  }

  function containScale(vw, vh, iw, ih) {
    return Math.min(vw / iw, vh / ih);
  }

  function layout() {
    var vw = view.clientWidth, vh = view.clientHeight;
    if (!vw || !vh) return;
    var n = naturalSize();
    var s = containScale(vw, vh, n.w, n.h) * zoom;
    var w = n.w * s, h = n.h * s;
    // 平移夹紧：画面比视口大时，偏移最多到「刚好铺满视口」，不会拖出黑缝。
    var maxX = Math.max(0, (w - vw) / 2);
    var maxY = Math.max(0, (h - vh) / 2);
    panX = Math.max(-maxX, Math.min(maxX, panX));
    panY = Math.max(-maxY, Math.min(maxY, panY));
    img.style.width = w + 'px';
    img.style.height = h + 'px';
    img.style.left = ((vw - w) / 2 + panX) + 'px';
    img.style.top = ((vh - h) / 2 + panY) + 'px';
  }

  // ★竖屏默认完整显示（zoom=1）：用户反馈竖屏 cover 只剩画面中央约 1/4 宽度，
  //   NPC 是全的但**下方对话选项框被裁掉** → 读不全、点不到。
  //   横屏默认填满（沉浸）。之后随时可捏合/点「画面」按钮自由切换。
  function initZoomForOrientation() {
    var vw = view.clientWidth, vh = view.clientHeight;
    if (!vw || !vh) { zoom = 1; return; }
    var n = naturalSize();
    var lo = containScale(vw, vh, n.w, n.h);
    var hi = Math.max(vw / n.w, vh / n.h);
    zoom = (vh > vw) ? 1 : (hi / lo);   // 竖屏看全，横屏填满
    panX = 0; panY = 0;
    layout();
  }

  // 提示气泡：所有「已发送/失败/已忽略」类反馈都走这里 —— 常驻 2.6 秒后自动消失，
  // 不占屏幕（用户 2026-10-14 要求撤掉所有常驻状态行，反馈改为临时气泡）。
  var hintTimer = 0;
  function hint(msg) {
    if (!hintEl) return;
    hintEl.textContent = msg;
    hintEl.style.display = 'block';
    clearTimeout(hintTimer);
    hintTimer = setTimeout(function () { hintEl.style.display = 'none'; }, 2600);
  }

  // ---- 单击透传 ----
  function sendTap(clientX, clientY) {
    var rect = img.getBoundingClientRect();
    if (!rect.width || !rect.height) return;
    // ★因为布局完全由 JS 决定，rect 就是画面本身 → 两行除号即得归一化坐标。
    //   落在黑边上（rect 外）算出 <0 或 >1，直接忽略，避免误点。
    var u = (clientX - rect.left) / rect.width;
    var v = (clientY - rect.top) / rect.height;
    if (u < -0.01 || u > 1.01 || v < -0.01 || v > 1.01) {
      hint('点在画面外的黑边，已忽略');
      return;
    }
    u = Math.max(0, Math.min(1, u));
    v = Math.max(0, Math.min(1, v));
    tapCount++;
    var url = CTRL + '/tap?x=' + u.toFixed(4) + '&y=' + v.toFixed(4)
      + '&cover=1&t=' + Date.now();
    fetch(url, { cache: 'no-store' }).then(function (r) {
      hint('已点画面 (' + u.toFixed(2) + ',' + v.toFixed(2) + ') #' + tapCount
           + ' · ' + zoom.toFixed(1) + '×（' + r.status + '）');
    }).catch(function (e) {
      hint('点击发送失败: ' + e);
    });
  }

  // ---- 手势状态 ----
  // ★2026-10-14：长按的用途从「唤出面板」变成「吞掉这次点击」。
  //   面板已按用户要求撤掉（只留常驻输入框+发送），但长按检测必须保留 ——
  //   否则长按画面后抬手会被当成普通点击，直接误点到游戏里的对话选项。
  var TAP_SLOP = 10;      // 超过这个位移就判为拖动，不再当点击
  var LONG_SLOP = 24;     // 长按的取消阈值必须比 TAP_SLOP 宽（真机微抖极常见）
  var LONG_MS = 600;      // 长按阈值
  var ptrs = {};          // pointerId -> {x,y}
  var ptrCount = 0;
  var gesture = null;     // {type, x0,y0,lx,ly, moved, consumed}
  var lpTimer = 0;
  var pinch0 = null;      // 捏合开始时的快照

  function firstTwo() {
    var ids = Object.keys(ptrs);
    return [ptrs[ids[0]], ptrs[ids[1]]];
  }
  function pdist(a, b) {
    var dx = a.x - b.x, dy = a.y - b.y;
    return Math.sqrt(dx * dx + dy * dy) || 1;
  }
  function clearLp() { if (lpTimer) { clearTimeout(lpTimer); lpTimer = 0; } }

  // ★touch-action:none 是 pinch 自定义处理的前提：
  //   不设它浏览器会自己做页面级双指缩放，我们的捏合就收不到 pointermove。
  // ★监听挂在 #view 而不是 #img —— 因为画面是 contain 时四周有黑边，
  //   挂在 img 上会让「从黑边起手的捏合」完全收不到事件（用户很自然地
  //   会从屏幕任意处起手）。坐标换算仍用 img 的 rect，落在黑边上会被
  //   sendTap 的范围检查拦下并提示，不会误点游戏。
  img.style.touchAction = 'none';
  view.style.touchAction = 'none';
  // ★防 iOS Safari 的橡皮筋/页面级缩放，否则捏合会被浏览器抢走。
  view.addEventListener('gesturestart', function (e) { e.preventDefault(); });

  view.addEventListener('pointerdown', function (e) {
    e.preventDefault();
    try { view.setPointerCapture(e.pointerId); } catch (err) { }
    ptrs[e.pointerId] = { x: e.clientX, y: e.clientY };
    ptrCount++;

    if (ptrCount === 1) {
      gesture = { type: 'tap', x0: e.clientX, y0: e.clientY,
                  lx: e.clientX, ly: e.clientY, moved: false, consumed: false };
      clearLp();
      // ★长按 = 吞掉这次点击（不再唤出面板，面板已撤）。
      //   为什么必须吞：长按画面后抬手若当成普通点击，会直接误点到游戏里的
      //   对话选项（看看/聊聊/再见），用户根本没想点它。
      lpTimer = setTimeout(function () {
        lpTimer = 0;
        // ★判定条件是 type==='tap'，**不是 !moved**（2026-10-14 离线验证抓到的坑）：
        //   moved 只表示「位移超过 TAP_SLOP，不当点击」（防手抖误点游戏），
        //   但长按理应容忍这种微抖 —— 真正该取消长按的是「明显拖动」，
        //   而那种情况在 pointermove 里已经 type='pan' 并 clearLp() 了，
        //   计时器根本不会走到这里。写成 !gesture.moved 会让微抖后的
        //   长按静默失效。
        if (gesture && gesture.type === 'tap' && !gesture.consumed) {
          gesture.consumed = true;
          hint('已忽略长按（轻点画面即可操作）');
        }
      }, LONG_MS);
    } else if (ptrCount === 2) {
      var a = firstTwo();
      var ir = img.getBoundingClientRect();
      var cx = (a[0].x + a[1].x) / 2, cy = (a[0].y + a[1].y) / 2;
      pinch0 = {
        dist: pdist(a[0], a[1]),
        zoom: zoom, panX: panX, panY: panY,
        // ★★必须记下**捏合开始那一刻**的显示尺寸，而不是每次 move 现读 rect。
        //   离线验证抓到的真 bug：现读 rect 时，第二根手指再动一次就会拿
        //   「已经放大过的 rect」再乘一次缩放比 → 缩放被**累乘**，锚点持续漂移
        //   （实测 u:0.296→0.194、v:0.044→0.272，完全跑飞）。
        //   锚点公式必须是「起始尺寸 × 总缩放比」的一次性计算。
        w0: ir.width || 1, h0: ir.height || 1,
        // 两指中点所对应的「画面内归一化坐标」——缩放时靠它保持所指内容不动
        u: (cx - ir.left) / (ir.width || 1),
        v: (cy - ir.top) / (ir.height || 1),
        cx: cx, cy: cy,
        vw: view.clientWidth, vh: view.clientHeight,
        vleft: view.getBoundingClientRect().left,
        vtop: view.getBoundingClientRect().top
      };
      if (gesture) gesture.type = 'pinch';
    }
  }, { passive: false });

  view.addEventListener('pointermove', function (e) {
    if (!ptrs[e.pointerId]) return;
    ptrs[e.pointerId].x = e.clientX;
    ptrs[e.pointerId].y = e.clientY;

    if (ptrCount >= 2 && pinch0) {
      e.preventDefault();
      var a = firstTwo();
      var nz = pinch0.zoom * (pdist(a[0], a[1]) / pinch0.dist);
      nz = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, nz));
      var f2 = nz / pinch0.zoom;              // 相对捏合起始的总缩放比
      var vw = view.clientWidth, vh = view.clientHeight;
      // ★全部基于 pinch0 的起始快照计算，绝不读当前 rect（否则缩放被累乘）。
      var w1 = pinch0.w0 * f2, h1 = pinch0.h0 * f2;
      var cx = (a[0].x + a[1].x) / 2, cy = (a[0].y + a[1].y) / 2;
      // 让画面内 (pinch0.u, pinch0.v) 这一点继续落在当前两指中点下
      var leftInView = (cx - pinch0.vleft) - pinch0.u * w1;
      var topInView = (cy - pinch0.vtop) - pinch0.v * h1;
      zoom = nz;
      panX = leftInView - (vw - w1) / 2;
      panY = topInView - (vh - h1) / 2;
      layout();
      return;
    }

    if (ptrCount === 1 && gesture) {
      if (gesture.type === 'tap') {
        var dxLp = Math.abs(e.clientX - gesture.x0);
        var dyLp = Math.abs(e.clientY - gesture.y0);
        if (dxLp > TAP_SLOP || dyLp > TAP_SLOP) {
          // ★★超过 TAP_SLOP：不再当点击（防手抖误点游戏），但**不取消长按**。
          //   2026-10-14 用户实测踩过：旧代码在这里直接 clearLp() ——
          //   长按期间手指只要轻微抖一下（真机上极常见，12px 足够），
          //   长按计时器就被取消。
          //   ⇒ 只有超过更宽的 LONG_SLOP（明显是拖动）才转 pan 并取消长按；
          //     10~24px 的中间地带 = 不点击、不平移，但长按仍然有效。
          gesture.moved = true;
          if (dxLp > LONG_SLOP || dyLp > LONG_SLOP) {
            gesture.type = 'pan';
            clearLp();
          }
        } else {
          return;
        }
      }
      if (gesture.type === 'pan') {
        e.preventDefault();
        panX += e.clientX - gesture.lx;
        panY += e.clientY - gesture.ly;
        gesture.lx = e.clientX; gesture.ly = e.clientY;
        layout();
      }
    }
  }, { passive: false });

  function endPointer(e) {
    if (!ptrs[e.pointerId]) return;
    delete ptrs[e.pointerId];
    ptrCount = Math.max(0, ptrCount - 1);
    if (ptrCount < 2) pinch0 = null;
    if (ptrCount > 0) return;

    clearLp();
    var g = gesture;
    gesture = null;
    // ★只有「没移动、没被长按吃掉、没参与捏合」的第一次按下抬手才算点击。
    //   拖动和长按都不会误触游戏 —— 这正是上一版面板乱冒 + 点错的成因。
    if (g && g.type === 'tap' && !g.moved && !g.consumed) {
      sendTap(e.clientX, e.clientY);
    }
  }
  view.addEventListener('pointerup', endPointer);
  view.addEventListener('pointercancel', endPointer);

  // 视口尺寸变化（转屏、面板显隐导致 #view 高度变化）都要重排，
  // 否则画面会被压变形或偏移。ResizeObserver 比 window.resize 可靠
  //（面板 display 切换不触发 resize，但会触发 #view 的尺寸变化）。
  if (window.ResizeObserver) {
    new ResizeObserver(function () { layout(); }).observe(view);
  } else {
    window.addEventListener('resize', function () { layout(); });
  }
  window.addEventListener('orientationchange', function () {
    setTimeout(initZoomForOrientation, 250);
  });

  function connect() {
    img.onload = function () {
      hideOverlay();
      retry = 1000;
      // ★首帧到达后才布局：此前 naturalWidth=0，算出来的尺寸是错的。
      if (!zoomed) { zoomed = true; initZoomForOrientation(); }
      else { layout(); }
    };
    img.onerror = function () {
      showOverlay('画面中断，正在重连…');
      setTimeout(connect, retry);
      retry = Math.min(retry * 1.6, 6000);
    };
    // 加时间戳避免浏览器缓存住旧连接
    img.src = '/mjpeg?t=' + Date.now();
  }
  var zoomed = false;
  connect();

  // ★★ 声音下行播放器。
  //   唯一真源 = E:\a1c\latencylab\webaudio_player.js，由 tools/embed_player.py
  //   逐字嵌入（含 C# 逐字字符串的引号转义与自检）。★改播放器只改真源，
  //   然后重跑嵌入脚本 + 重跑 latencylab 实验室验证，永远不要手改这两行之间。
  //   【为什么换掉 <audio>】实验室实测：<audio> 对流式 WAV 固定预缓冲约 2.2 秒
  //   （与网络无关），NPC 声音比画面晚一大截；Web Audio 手动调度后端到端 170ms。
  // ===AUDIO_PLAYER_BEGIN===
// ★★ 声音下行播放器：fetch 流式读 WAV(PCM) + Web Audio 手动调度。
//
// ★唯一真源 = 本文件。由 tools/embed_player.py 逐字嵌入 PhonePage.cs（含引号转义），
//   实验室 E:\a1c\latencylab\lab.js 用 addInitScript 直接注入同一份内容执行。
//   ★改播放器只改这里，然后重跑实验室 + 重跑嵌入脚本，永远不要手改两份。
//
// 【为什么换掉 <audio> 元素（2026-10-14，用户实测「声音延迟明显」）】
//   实验室实测（本地链路、协议逐字复刻、无网络瓶颈）：
//   <audio> 对流式 WAV **固定预缓冲约 2.2 秒**才起播
//   （buffered.end(0)-currentTime 稳态 2.05~2.27s，与网络好坏无关）。
//   这是浏览器媒体元素内部的缓冲策略，前端没有任何开关能调小
//   ⇒ NPC 声音比画面晚一大截，用户感受到的「没和游戏同步」。
//
// 【方案】自己剥 WAV 头、自己切 PCM 块、自己按 AudioContext 时钟调度：
//   fetch('/audio') 流式收字节（服务端 AudioForwarder.ServeStream 本来就按
//   实时节流推送，且积压超 320ms 会丢弃陈旧数据）
//   → 每凑够 40ms 采样建一个 AudioBuffer，接在上一个块结尾播放
//   → 缓冲低于 120ms 目标就补齐；落后超过目标就跳到 currentTime+30ms
//     （丢过时数据，与服务端 RingBuffer.TrimTo 同思路：通话场景宁可丢开头）
//   ⇒ 端到端延迟 ~150ms，缓冲深度恒定，不随播放时间累积。
//
// 【双路径与自动回退（2026-10-14 深夜，用户实测「完全没声音」）】
//   现象：服务端日志「音频客户端已连接→已断开」每秒级循环，说明 fetch 每次
//   拿到响应头后就被客户端断开。头号原因：**浏览器不支持 fetch 响应流式读取**
//   （res.body.getReader）——iOS Safari <16.4 直接没有 ReadableStream；
//   部分 webview 同样缺失。第二原因：AudioContext 被自动播放策略挂起。
//   ★没声音比 2.2 秒延迟严重得多 ⇒ 检测到流式不可用/连续失败即自动回退
//     <audio> 元素（旧方案，用户验证过能出声），并 hint 告知用户。
//   ★force() 可手动 A/B：控制台跑 __audioDebug.force('audio-element') /
//     __audioDebug.force('webaudio') 后刷新，用来坐实是哪条路径的问题。
//
// 【风险与对策】
//   · AudioContext 自动播放限制（iOS）：加载即建，touchstart/click/
//     visibilitychange 时 resume()（★resume() 返回的 Promise 要 catch，
//     无手势时 iOS 会 reject，不 catch 会在控制台留未捕获错误）。
//   · 服务端首音前最多阻塞 20 秒后返回 503（带 Content-Length 的文本）：
//     fetch 正常 resolve，status 503 → 退避重连（1s→8s 封顶），连上即归零；
//     与原 <audio> 的 error 重连等价，但不再有「error 后永不重试」的旧坑。
//   · PCM 按帧对齐切块，绝不切半个采样（aligntest 抓过的「噼里啪啦」教训）。
//   · createBuffer(1, n, rate) 声明源采样率，播放时 Web Audio 自动重采样到
//     设备采样率，无需手工重采样。
//   · 格式防御：服务端固定推 48k/16bit/mono（日志实证），但浏览器侧仍按
//     WAV 头字段解析，兼容 8/16/24/32bit、float、多声道（下混）；不认识的
//     格式直接取消本次连接退避重试，绝不放噪音出来。
(function () {
  // ★URL 可调（项目纪律：先测量后调参，每项独立开关）：
  //   ?tgt=NN 目标缓冲深度；?keep=NN 挂起期间保留的尾巴时长。
  //   手机 URL 直接加参数就能 A/B，看完诊断条「感知≈Nms」再定默认值，
  //   不用每次重新部署。
  function qnum(name, def, lo, hi) {
    var m = new RegExp('[?&]' + name + '=(\\d+)').exec(location.search);
    if (!m) return def;
    var v = parseInt(m[1], 10);
    return isFinite(v) ? Math.max(lo, Math.min(hi, v)) : def;
  }
  // 120→80（round26）：实测 fresh-load 感知延迟≈缓冲深度+输出，深度是大头；
  //   80ms≈2 个块宽的抗抖余量（Wi-Fi 抖 >80ms 才欠载跳变），比 120 少 40ms。
  var TARGET_MS = qnum('tgt', 80, 20, 500);   // 目标缓冲深度（ms）：高于它就丢到最新
  var MIN_MS = 30;       // 启动/落后时的起播缓冲
  var BLOCK_MS = 40;     // 聚合块（ms）：权衡调度节点数与起播粒度
  // ★淡入淡出时长（round27「刷新爆音」主修）：起播/停止时的 25ms 增益渐变，
  //   消掉「满音量任意相位硬起播」和「播放中页面被销毁硬切断」的直流阶跃
  //   （用户听感：刷新网页有时「嗒」一声，有时正常——取决于刷新瞬间旧页面
  //   是否正好在播非零波形）。25ms 低于人耳可辨的起振/收尾阈值，对话音
  //   头尾无损；?fade=0 可整体关闭做 A/B。
  var FADE_S = qnum('fade', 25, 0, 500) / 1000;
  // ★round28「呲呲呲电流声」自愈参数（2026-10-15 23:03 截图驱动）：
  //   interactive 档 outputLatency 正常 10~30ms；截图实测 2584ms —— 安卓
  //   Chrome 的 AAudio 后端自适应缓冲机制会把输出缓冲撑到秒级（Chromium
  //   issue 40860631「安卓 Chrome Web Audio 输出延迟极高」、Chrome 136
  //   禁用该机制的修复记录均证实这是已知问题类）。render quantum 与 40ms
  //   块调度在秒级缓冲下失配 ⇒ 电流声 + 原声消失 + 持续 + 偶尔自愈。
  //   判据只用「读数 + 持续时长」，不猜内部机制；?olat=0 可关闭自愈。
  var OUT_LAT_MS = qnum('olat', 500, 0, 5000);  // outputLatency 自愈阈值（ms）
  var OUT_LAT_BAD_MS = 5000;   // 持续多久才动手（防单次采样抖动误伤）
  var OUT_LAT_LOOP = 2;        // 窗口内自愈这么多次 = 重建也没用 → 兼容路径
  var OUT_LAT_WINDOW = 30000;  // 循环判据窗口（ms）

  var ctx = null, gain = null;
  var nextStart = 0;     // AudioContext.currentTime 基准；0 = 尚未起播
  var ctxEpoch = 0;      // performance.now()/1000 - ctx.currentTime（ctx 时钟 0 的真实时刻，秒）
  var streamBytes = 0;   // 已调度的 PCM 字节（流位置，用于算端到端延迟）
  var rate = 48000, channels = 1, bits = 16, isFloat = false, frameBytes = 2;
  var wavOk = false;     // WAV 头已剥离
  var tail = null;       // 跨 read 块的字节残余（Uint8Array）
  var pumping = false;
  var retry = 0;
  var everOk = false;
  var curReader = null;

  // ---- 双路径状态 ----
  // mode: 'webaudio'（低延迟，默认）| 'audio-element'（兼容保底，?ae=1 强制）
  //
  // ★★路径切换史（两次都由用户安卓机实测驱动）：
  //   ① 2026-10-14 深夜：Web Audio 诊断条「读 7409K」vs「块 19」——数据全部到达
  //      却几乎不变成播放块。根因后已查明并修复：**安卓 Chrome 无手势默认挂起
  //      AudioContext**，schedule() 在 ctx 非 running 时整块丢弃（现改为保留最近
  //      KEEP_MS + 恢复补播 + 挂起期间每 3s 反复提示点画面）。当时为保出声临时
  //      把默认切成 <audio>（浏览器 media pipeline，声音约晚 2 秒）。
  //   ② 2026-10-15：挂起根因已修；且 <audio> 的 ~2.2s 是手机侧延迟的绝对大头，
  //      远超 TARGET_MS/maxBacklog 那几十 ms 的参数空间。用户加 latencyHint
  //      「没感觉」正是因为默认走的不是 Web Audio 路径。
  //      ⇒ 默认切回 webaudio；兜底：?ae=1 仍可强制兼容路径，且新增
  //      「数据到达但不播放」自动回退（8s 内 reads>200KB 且 blocks==0 → 切
  //      <audio>），防止 10-14 那类蒸发再次表现为「没声音」。
  var mode = /[?&]ae=1\b/.test(location.search) ? 'audio-element' : 'webaudio';
  var fallbackReason = '';
  var streamFails = 0;    // 连续流式失败次数（fetch/读取异常），达 3 次回退
  var audEl = null;       // 回退路径的 <audio> 元素（动态创建）
  var audRetry = 0;
  var audEverOk = false;
  var ctxHinted = false;  // 「点画面开声音」提示（兼容旧引用）
  var lastDropHintAt = 0; // 上次「点画面开声」提示时刻（挂起期间每 3s 重复）
  var keepBuf = [];       // ctx 挂起期间保留的最近块（Float32Array），恢复后补播
  var keepPending = false;// 有待补播的保留内容
  var lastCtxRunning = true;  // 上次 schedule 时 ctx 是否 running（挂起计数边沿检测）
  // 600→250（round26）：实测锁屏恢复后缓冲深度被钉死在 keep+TARGET≈760ms，
  //   且「故意不追齐」⇒ 挂起一次就永久 +640ms 延迟直到刷新页面（20:41 用户
  //   截图：缓冲763ms 就是这么来的）。250ms 仍能听到「最后半句」，
  //   把挂起恢复后的感知延迟压到 ≈330ms。?keep=600 可换回旧行为。
  var KEEP_MS = qnum('keep', 250, 0, 2000);  // 保留时长：一点画面能听到「最后 0.25 秒」

  var stats = {
    blocks: 0,       // 已调度的音频块数
    jumps: 0,        // 非首次的「跳到最新」次数（网络抖动/断流恢复）
    maxBufMs: 0,     // 观测到的最大缓冲深度
    badFmt: 0,       // 不认识的 WAV 格式次数
    firstByteAt: 0,  // 首个 PCM 字节到达时刻（Date.now）
    connAt: 0,       // 连接建立时刻（performance.now()/1000，秒）——端到端延迟基准
    lastReadAt: 0,   // 最近一次读到字节的时刻（Date.now）——「流还活着吗」判据
    reads: 0, bytes: 0,
    readLog: [],       // 最近 40 次 read：{at, len, total}（诊断/实验室用）
    schedLog: [],      // 最近 40 个块：{streamMs, at} 起播时刻（诊断/实验室用）
    droppedMs: 0,      // ★ctx 非 running 被丢弃的音频毫秒数（2026-10-14「没声音」
                       //   故障关键指标：reads/bytes 照涨但 blocks 不涨 = 这里在涨）
    ctxSuspendCount: 0, // ctx 进入非 running 状态的次数
    fadeIns: 0,        // 起播淡入次数（round27：每次从零起播/恢复各一次）
    fadeOuts: 0        // 停止淡出次数（round27：刷新/切后台/重建/回退各一次）
  };

  // ★起播锚定淡入（round27b，22:40 用户截图驱动）：resume 里的 fadeIn 锚
  //   now+25ms，而起播在 now+TARGET_MS(80ms)——淡入在起播之前就淡完了，
  //   首块（含挂起恢复的 keepBuf 补播首块）仍以满电平任意相位起播 →
  //   「噗」一声。这就是 round27 部署后「淡入3 仍有爆音」的漏洞。
  //   这个变体锚定**起播时刻**：先把 gain 硬拉到 0（调用点保证此刻没有
  //   在播的声音——nextStart=0 就是时间轴空白），再线性涨到 1，起播那
  //   刻 gain 正在从 0 起涨，阶跃被渐变吃掉。
  //   ★调用点仅限「起播」：playBlock 的 nextStart===0 分支（覆盖
  //     fresh-load / 重建 / keepBuf 补播三种起播）。绝不在播放中调用——
  //     setValueAtTime(0) 会硬切正在播的流（那才是真爆音）。
  function fadeInAt(when) {
    if (!ctx || !gain) return;
    try {
      var t = ctx.currentTime;
      gain.gain.cancelScheduledValues(t);
      gain.gain.setValueAtTime(0, t);
      gain.gain.linearRampToValueAtTime(1, when);
      stats.fadeIns++;
    } catch (e) { /* 老浏览器：退回硬起播 */ }
  }

  // ---- 僵尸 AudioContext 检测与自愈（2026-10-15 round24）----
  // ★安卓 Chrome 已知 bug：页面从后台/打断恢复后 ctx.state==='running'，
  //   但渲染时钟冻结（currentTime 不动）——块照排、数据照收，就是没声音。
  //   10-15 用户报「没声音」时诊断条正是这个形态（ctx=running 块1621 缓冲800ms），
  //   但当时诊断条不显示 currentTime，无法证实/证伪（那次的直接诱因是游戏退出）。
  //   判据 = 数据在流（playBlock 被调用）而 currentTime 不动（墙钟在走）。
  //   自愈 = 业界标准解法：close + 重建 AudioContext（nextStart/streamBytes 归零，
  //   WAV 解析状态与读取循环保留，重建后从下一个块重新起播，无陈旧音频）。
  var lastCt = null;      // 上次采样到的 ctx.currentTime
  var lastCtWall = 0;     // 上次采样的墙钟（Date.now）
  var clockFrozenMs = 0;  // 当前连续冻结时长（诊断条可见）
  var ctxRebuilds = 0;    // 自动重建次数（≥2 说明重建也没用 → 回退兼容路径）
  var lastNetHintAt = 0;  // 网络类提示的节流时刻（5s 内不重复刷）

  // 网络类提示统一节流：重连/断开/503 都走这里，5 秒一条不刷屏
  function netHint(msg) {
    if (Date.now() - lastNetHintAt < 5000) return;
    lastNetHintAt = Date.now();
    hint(msg);
  }

  // 返回 true = 检测到冻结且已处理（调用方应丢弃本块：重建后从下一块起播）
  function zombieCheck() {
    if (!ctx || ctx.state !== 'running') return false;
    var ct = ctx.currentTime, wall = Date.now();
    if (lastCt === ct) {
      clockFrozenMs = wall - lastCtWall;
    } else {
      lastCt = ct; lastCtWall = wall; clockFrozenMs = 0;
    }
    // ★必须 visible 门控：页面隐藏时 Chrome 合法冻结时钟（此时重建必然被
    //   拒），等回前台再说；数据恢复流动后本函数自然继续检测。
    if (clockFrozenMs > 2000 && document.visibilityState === 'visible') {
      if (ctxRebuilds >= 1) { switchToAudioEl('clock-frozen'); return true; }
      rebuildCtx('clock-frozen');
      return true;
    }
    return false;
  }

  function rebuildCtx(reason) {
    // ★淡出后再 close（round27）：僵尸重建原来直接 close，正在播的波形被
    //   硬切 → 用户听到「嗒」后才进入新通道。fadeOut('close') 捕获旧引用，
    //   40ms 后 close 旧 ctx；下面立即建的新 ctx 不受影响。
    fadeOut('close');
    ctx = null; gain = null;
    nextStart = 0;          // 时间轴基准作废（否则拿着旧时钟值排到假未来）
    streamBytes = 0;        // 端到端测量基准重来
    keepBuf = []; keepPending = false;
    lastCt = null; lastCtWall = 0; clockFrozenMs = 0;
    ctxRebuilds++;
    ensureCtx();
    resume();
    hint('声音通道已自动重建（' + reason + '）。若仍没声：点一下画面，'
      + '并检查手机侧边静音开关；持续如此请把这个画面截图给开发者。');
  }

  // ---- outputLatency 异常态检测与自愈（round28，「呲呲呲」最后一轮）----
  // 症状（2026-10-15 23:03 用户截图 + PC 侧声音正常二判一）：服务端全指标
  //   健康、电脑游戏声音正常，手机侧听到持续电流声且游戏原声消失，偶尔
  //   自愈，刷新页面随机恢复。与既有形态都不同：不是挂起（ctx=running）、
  //   不是时钟冻结（时钟在走）、不是图断（块照排且本该出声）。
  // 假设（高置信，未直接证实）：ctx 输出链路进入巨大缓冲异常态——
  //   诊断条 outputLatency 2584ms（正常 24ms）就是它的直接读数；render
  //   quantum 与 40ms 块调度在秒级缓冲下失配，数字上是延迟爆炸，听感上
  //   是噪声而非单纯延迟。
  // 动作：持续 5s 超阈值 → 重建 ctx（相当于「软刷新」：不重载 MJPEG 画面、
  //   不丢连接槽位、读取循环保留，从下一个块重新起播）；窗口内坏 ≥2 次
  //   → 切 audio-element 兼容路径（浏览器 media pipeline，从未出现此怪
  //   毛病，代价是约 2s 延迟——保底出声优先）。
  var outLatBadSince = 0;    // 首次超阈值时刻（performance.now() ms），0=当前正常
  var outLatRebuilds = 0;    // 因 outputLatency 自动重建的次数（诊断可见）
  var outLatRebuildAt = [];  // 近期重建时刻（Date.now ms），循环判据用
  function outLatCheck() {
    if (!ctx || mode !== 'webaudio' || OUT_LAT_MS <= 0) return false;
    var o = outLatMs();
    if (o > OUT_LAT_MS) {
      var nowMs = performance.now();
      if (!outLatBadSince) outLatBadSince = nowMs;
      if (nowMs - outLatBadSince < OUT_LAT_BAD_MS) return false;  // 还没持续够
      var now = Date.now();
      while (outLatRebuildAt.length && now - outLatRebuildAt[0] > OUT_LAT_WINDOW) {
        outLatRebuildAt.shift();   // 挤出窗口外的旧记录
      }
      outLatRebuildAt.push(now);
      outLatRebuilds++;
      if (outLatRebuildAt.length >= OUT_LAT_LOOP) {
        switchToAudioEl('out-lat-loop');
        return true;
      }
      rebuildCtx('out-lat');
      return true;
    }
    outLatBadSince = 0;   // 恢复正常：清零，下次异常重新计时
    return false;
  }

  // ---- 手动刷新声音（round28 保底：手机用户不会/不习惯刷新网页）----
  // 用户原话：「很多玩家不知道手机网页怎么刷新，或者不习惯在手机上刷新页面」。
  // 为什么不是 location.reload()：那会重载整页——MJPEG 画面断开重连、6 条
  //   同源连接槽位全腾空再抢、输入框内容丢失；而且「点击刷新按钮」本身
  //   就是用户手势，一步绕过无手势挂起，比重载整页更稳。
  // 本函数 = 断开当前音频流 + 重建 ctx + 重连音频流；画面与打字不受影响。
  function rebuildAudio(reason) {
    if (mode === 'audio-element') {
      // 兼容路径的等价操作：audio 元素重新拉流
      hint('正在重新连接声音…');
      attemptAudioEl();
      return;
    }
    if (curReader) { try { curReader.cancel(); } catch (e) { } }
    rebuildCtx(reason);
    // 重连音频流（wavOk 等 WAV 解析状态沿用；attempt 内会重置
    // connAt/streamBytes 测量基准，round27c）
    if (!pumping) { pumping = true; attempt(); }
    hint('声音通道已手动重建（' + reason + '）。若仍没声：点一下画面，'
      + '并检查手机侧边静音开关；持续如此请把这个画面截图给开发者。');
  }

  // ---- 右上角「⟳ 刷新声音」按钮（round28 保底）----
  // 半透明小按钮不挡画面（MJPEG 全屏，右上角通常是画面边缘信息区之外）。
  // 常驻显示：不挂在「服务是否健康」分支里——恰恰是出问题的时刻最需要它
  // （本项目用教训换来的原则：UI 反馈不要挂在健康条件分支上）。
  function bindRefreshButton() {
    if (document.getElementById('audi-refresh')) return;
    var b = document.createElement('div');
    b.id = 'audi-refresh';
    b.textContent = '⟳';
    b.title = '刷新声音';
    b.style.cssText = 'position:fixed;right:8px;top:8px;z-index:9998;'
      + 'background:rgba(0,0,0,.55);color:#fff;font:16px/1 sans-serif;'
      + 'padding:7px 10px;border-radius:6px;cursor:pointer;'
      + 'user-select:none;-webkit-user-select:none;touch-action:manipulation;';
    b.addEventListener('click', function (ev) {
      // ★stopPropagation：否则冒泡到 document 的 click 处理器（resume），
      //   与 rebuildAudio 内部的 resume 重复——无害但没必要；preventDefault
      //   防某些浏览器把 div 点击当文本选择/拖拽。
      if (ev.preventDefault) ev.preventDefault();
      if (ev.stopPropagation) ev.stopPropagation();
      rebuildAudio('manual');
    });
    document.body.appendChild(b);
  }

  // ---- 页面提示（复用主脚本的 #hint 气泡；显示 6 秒比普通反馈久）----
  function hint(msg) {
    try {
      var h = document.getElementById('hint');
      if (!h) return;
      h.textContent = msg;
      h.style.display = 'block';
      setTimeout(function () { h.style.display = 'none'; }, 6000);
    } catch (e) { /* 提示失败不影响播放 */ }
  }

  function ensureCtx() {
    if (ctx) return ctx;
    var AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;
    // ★latencyHint:'interactive'（2026-10-15 音频延迟优化第 1 项，用户已确认）：
    //   请求浏览器给**最小的输出 buffer**；不传参数默认按 playback 档给，偏大。
    //   老版 webkitAudioContext 不认识构造参数 → try/catch 回退；
    //   即使静默忽略也只是回到默认行为，无副作用。
    try { ctx = new AC({ latencyHint: 'interactive' }); }
    catch (e) { ctx = new AC(); }
    // ★单位统一为秒：performance.now() 是毫秒、ctx.currentTime 是秒，
    //   直接相减会得到 49199.999 这种四不像（实验室第一版踩过，
    //   端到端算出 -25 秒全是它害的）。
    ctxEpoch = performance.now() / 1000 - ctx.currentTime;
    gain = ctx.createGain();
    gain.connect(ctx.destination);
    // ★初值 0（round27）：等 resume 成功后 fadeIn 淡入到 1。直接给 1 的话，
    //   「页面加载即有声音」的场景（桌面无手势）首块就是满音量硬起播。
    gain.gain.value = 0;
    return ctx;
  }
  function resume() {
    // ★不用 === 'suspended'：iOS 打断后是 'interrupted'，同样需要恢复
    if (!ctx) return;
    var go = function () { fadeIn(); };
    if (ctx.state !== 'running') {
      try {
        var p = ctx.resume();
        // ★无手势时 iOS 会 reject 这个 Promise；不 catch 会在控制台
        //   留一条未捕获错误（不影响功能，但污染诊断输出）
        // ★淡入必须等 resume **成功**后再启动：suspended 时时钟冻结，
        //   ramp 排在冻结时钟上不会执行，等手势恢复后一次补完 = 没有渐变。
        if (p && p.then) p.then(go, function () { /* 等下一次手势 */ });
        else if (ctx.state === 'running') go();
      } catch (e) { /* 等下一次手势 */ }
    } else {
      go();   // 已 running（桌面无手势即 running / rebuild 后立即 running）
    }
  }

  // ---- 起播/停止渐变（round27「刷新爆音」主修）----
  // 爆音机制（2026-10-15 定位）：刷新网页时若旧页面正好在播放非零波形，
  //   页面销毁会连音频线程一起硬切 → 直流阶跃 → 「嗒」；恰好停在静音间隙
  //   就正常 —— 与用户报的「有时候出现有时候正常」完全吻合。服务端侧已
  //   排除（日志：所有连接「丢弃陈旧 0ms」、积压 14~54ms、格式链路全对）。
  // 两侧都修：起播淡入（首块不再满音量硬起）+ 停止淡出（销毁前先收敛到 0）。
  function fadeIn() {
    // ★递增令牌：作废所有未执行的延时 stop（resume 在淡出后的 40ms 窗口内
    //   赢了竞态——页面又可见了/切回 webaudio，迟到的 suspend 必须作废，
    //   否则把刚淡入起来的通道挂回去 → 静默）。
    fadeSeq++;
    if (!ctx || !gain) return;
    try {
      var t = ctx.currentTime;
      var v = (typeof gain.gain.value === 'number') ? gain.gain.value : 1;
      if (v >= 0.999) return;               // 已经满音：别扰动播放中的流
      gain.gain.cancelScheduledValues(t);
      gain.gain.setValueAtTime(v, t);
      gain.gain.linearRampToValueAtTime(1, t + FADE_S);
      stats.fadeIns++;
    } catch (e) { /* 老浏览器没有 AudioParam 方法：退回硬起播，不致命 */ }
  }
  // ★必须捕获当前 ctx/gain 的**局部引用**：rebuildCtx 会把外层 ctx/gain
  //   指向新对象，闭包晚一步执行就会 close/suspend 掉新 ctx（静默杀死
  //   刚重建的通道——症状是「重建了但从此没声」）。
  var fadeSeq = 0;   // 淡出令牌：fadeIn/resume 递增 → 之前的延时 stop 作废
  function fadeOut(stop) {
    var c = ctx, g = gain;
    if (!c || !g) return;
    try {
      var t = c.currentTime;
      var v = (typeof g.gain.value === 'number') ? g.gain.value : 1;
      stats.fadeOuts++;
      if (v > 0.001) {
        g.gain.cancelScheduledValues(t);
        g.gain.setValueAtTime(v, t);
        g.gain.linearRampToValueAtTime(0, t + FADE_S);
      }
      if (stop) {
        var my = ++fadeSeq;
        // 已淡完（v≈0）同步停；否则等 ramp 跑完再停——suspend 会冻结时钟，
        // 立即调用会把没跑完的 ramp 一起冻住（淡出变成硬切）。
        var delay = (v > 0.001) ? FADE_S * 1000 + 15 : 0;
        setTimeout(function () {
          // close 不设防：重建语义下旧 ctx 必须死，无条件执行。
          if (stop !== 'close' && my !== fadeSeq) return;
          doStop(stop, c);
        }, delay);
      }
    } catch (e) { /* 同 fadeIn：老浏览器退回硬停 */ }
  }
  function doStop(stop, c) {
    try { if (stop === 'close') c.close(); else c.suspend(); } catch (e) { }
  }

  // 采样帧（可能多声道/多种位深）→ 单声道 Float32
  function toFloat32(bytes, byteOff, frames) {
    var out = new Float32Array(frames);
    var dv = new DataView(bytes.buffer, bytes.byteOffset + byteOff);
    var ch = channels, step = bits / 8, fb = frameBytes;
    for (var i = 0; i < frames; i++) {
      var base = i * fb, s = 0;
      for (var c = 0; c < ch; c++) {
        var o = base + c * step;
        if (isFloat) s += dv.getFloat32(o, true);
        else if (bits === 16) s += dv.getInt16(o, true) / 32768;
        else if (bits === 8) s += (dv.getUint8(o) - 128) / 128;
        else if (bits === 24) {
          var b0 = dv.getUint8(o), b1 = dv.getUint8(o + 1), b2 = dv.getUint8(o + 2);
          var v = (b2 << 16) | (b1 << 8) | b0;
          if (v & 0x800000) v |= ~0xFFFFFF;
          s += v / 8388608;
        } else s += dv.getInt32(o, true) / 2147483648;
      }
      out[i] = s / ch;
    }
    return out;
  }

  function schedule(f32) {
    // ★★★ ctx 非 running（suspended / iOS interrupted）时**丢弃数据**：
    //   currentTime 冻结，块会全部排到「冻结时刻 + N×40ms」的假时间轴上，
    //   恢复后按序补播——用户听到的是几十秒前的陈旧声音（比没声音更糟）。
    //   ★nextStart 必须重置为 0：否则恢复后 first 分支不进、else 分支
    //     拿着几十秒前的 nextStart 当基准，全部块排到遥远的未来。
    //   ★丢弃必须计数（2026-10-14 深夜实测教训）：用户诊断条显示
    //     「ctx=running 块48 读4205 字节2344K」——25 秒数据只出 48 块，
    //     约 560 块在这里被静默丢弃（安卓 Chrome 无手势默认挂起 ctx，
    //     用户打开页面没点屏幕，NPC 说的话全进了这里）。reads/bytes
    //     照涨极具欺骗性，必须让丢弃量在诊断条上可见。
    if (ctx.state !== 'running') {
      // ★挂起计数（round27c）：边沿检测，只在「running→非 running」跳变时
      //   +1。「呲呲呲电流声+原声消失」故障的关键判据之一：这个数涨得凶 =
      //   ctx 在被系统反复挂起（音频焦点被抢），恢复时浏览器输出链路可能
      //   进异常态（23:03 截图实证：outputLatency 2584ms，正常 24ms）。
      if (lastCtxRunning) { stats.ctxSuspendCount++; lastCtxRunning = false; }
      nextStart = 0;
      // ★保留最近 KEEP_MS 毫秒（2026-10-14 深夜加）：以前挂起即全丢，
      //   用户打开页面没点屏幕 → NPC 整句话蒸发 → 体感「根本没这功能」。
      //   现在留个尾巴：一点画面先把最后 0.25 秒补播出来，再跟进实时。
      //   ★故意不追齐：保留块(默认≤250ms)播完后延迟 ≈250+80ms 恒定，
      //     追齐会让「已排未播」的实时块时间轴前移互相重叠 → 爆音。
      keepBuf.push(f32);
      // 从尾部向前攒，**加上某块会超过 KEEP_MS 就连它一起丢**。
      // ★原写法 `keptMs > KEEP_MS → splice(0, i)` 在 KEEP_MS 不是块长整数倍时
      //   会向上取整到块边界（250ms → 实留 280ms），注释说 250 代码给 280，
      //   典型「注释≠代码」。改为先判断再累加，严格 ≤KEEP_MS（250→留 6 块 240ms）。
      var keptMs = 0, cut = 0;
      for (var i = keepBuf.length - 1; i >= 0; i--) {
        var blk = keepBuf[i].length / rate * 1000;
        if (keptMs + blk > KEEP_MS) { cut = i + 1; break; }
        keptMs += blk;
      }
      if (cut > 0) keepBuf.splice(0, cut);
      keepPending = true;
      stats.droppedMs += f32.length / rate * 1000;   // 诊断：挂起期间到达的总量
      // ★流入滞后补偿（round27b）：e2e = now - connAt - 已调度时长，挂起期间
      //   丢弃的音频永远不会被调度，不补偿就**永久虚高**——22:40 用户截图
      //   「丢2s ⇒ 感知永远显示 2362ms 直到刷新」，把「曾经挂起过」误显示成
      //   「一直卡 2 秒」。connAt 前移丢弃量（每块都补，keepBuf 保留的也补），
      //   恢复后 e2e 立即回落到 ~0，诊断数字重新变诚实。
      stats.connAt += f32.length / rate;
      // ★提示从「一次性」改为「挂起期间每 3 秒重复」：一次性提示用户
      //   根本没注意（hint 6 秒就消失），这是「没声音」的头号原因，
      //   必须反复说。ctx 恢复后自然停。
      if (Date.now() - lastDropHintAt > 3000) {
        lastDropHintAt = Date.now();
        hint('声音通道未启动：点一下画面即可开声（若无声请检查手机侧边静音开关）');
      }
      return;
    }
    // ★恢复后先补播保留的尾巴（keepBuf 已在上面按 600ms 截过），
    //   再播当前块。保留块占用时间轴，实时块顺序接在其后。
    lastCtxRunning = true;
    if (keepPending && keepBuf.length) {
      var kb = keepBuf;
      keepBuf = [];
      keepPending = false;
      for (var j = 0; j < kb.length; j++) playBlock(kb[j]);
    }
    playBlock(f32);
  }

  function playBlock(f32) {
    // ★僵尸检测放最前面：时钟冻结时本块排了也白排（重建后从下一块起播，
    //   丢弃的这 40ms 由服务端实时流自然补上，不会漏内容）。
    if (zombieCheck()) return;
    // ★round28：outputLatency 异常态自愈（「呲呲呲电流声」形态）。放这里
    //   是因为它只在「有块在调度」时有意义，与 zombieCheck 同级；返回
    //   true = 已重建，本块丢弃（新 ctx 从下一块重新起播，服务端实时流
    //   自然补上这 40ms，不漏内容）。
    if (outLatCheck()) return;
    var buf = ctx.createBuffer(1, f32.length, rate);
    buf.copyToChannel(f32, 0);
    var src = ctx.createBufferSource();
    src.buffer = buf;
    // ★★★ src.connect(gain) —— 2026-10-15 定位的「幽灵没声音」根因，
    //   整条链路唯独这里断过，且一断就是好几轮（务必别删）：
    //   Web Audio 的规则——AudioBufferSourceNode 不 connect 进图，
    //   它就只是老老实实把样本放逐到虚空，start() 照成功、currentTime
    //   照走、块计数照涨，**但一个样本都不会到 destination**。
    //   ★为什么几轮都没抓到：所有诊断指标（读/字节/块/缓冲深度/端到端）
    //     全都测的是「调度」，没有一项要求图是通的！挂起理论、缓冲理论、
    //     latencyHint 全在解释一个它们影响不了的层面——症状（数据全到达、
    //     块全排了、就是没声）与它们全都兼容，所以每轮都「修好了另一个
    //     真 bug」而主症状不动。10-14「数据到达但不播放」的根因也是它，
    //     当时误判成无手势挂起（那个问题真实存在，但只是叠加因素）。
    //   ★判据（下次遇到同类先查这个）：ctx.state=running + currentTime
    //     实时在走 + 块照排 + 服务端照推 + 就是没声 ⇒ 99% 是图断了，
    //     不是缓冲/挂起/网络。在控制台跑一遍即证：
    //     __audioDebug.gain() 看有没有节点挂着；数 src.connect 调用。
    src.connect(gain);
    var now = ctx.currentTime;
    if (nextStart === 0) {
      // 首块：建立目标缓冲（用起播延迟换抗抖动能力）。
      // ★不能设 30ms——Wi-Fi 抖一下就欠载爆音；120ms 内端到端仍 ~160ms。
      nextStart = now + TARGET_MS / 1000;
      // ★起播锚定淡入（round27b）：resume 的 fadeIn 锚 now+25ms，在起播
      //   (now+TARGET) 前就淡完，阶跃照旧；这里锚定起播时刻本身。三种
      //   起播都走这个分支：fresh-load、rebuildCtx 后、keepBuf 补播首块。
      fadeInAt(nextStart);
    } else if (nextStart < now + MIN_MS / 1000) {
      // 真欠载（预约见底）：丢时间轴追齐到 now+MIN，丢的是间隔不是内容。
      // ★低于 TARGET 但 > MIN 时**顺序接播即可**：服务端按实时节流推，
      //   块间隔≈块时长，接播后深度 D 恒定（D_{N+1}=D_N+块长-到达间隔），
      //   既不累积也不见底 —— 曾误写成「低于 TARGET 就压到 MIN」，
      //   结果深度被钉死在 30~70ms（实验室 jumps=656 每块都跳），白扔抗抖能力。
      stats.jumps++;
      nextStart = now + MIN_MS / 1000;
    } else if (nextStart > now + 5.0) {
      // ★时间轴 runaway 追齐（2026-10-15 round24 补的漏洞）：原来只防
      //   「落后」（nextStart 见底）不防「遥遥领先」——页面后台化期间时钟
      //   冻结而块继续排到「冻结时刻+N×40ms」的假未来，恢复后 currentTime
      //   从冻结值继续，nextStart 已在几分钟后 → 全部块排到遥远未来 =
      //   静默且永远自愈不了（诊断条表现：缓冲深度几分钟、块照涨）。
      //   5s 阈值：合法堆积上限约 3s（服务端 maxBacklog 320ms + 内核 socket
      //   缓冲 + 恢复突发），runaway 是分钟级，差一个数量级，不会误伤。
      stats.jumps++;
      nextStart = now + TARGET_MS / 1000;
    }
    src.start(nextStart);
    nextStart += buf.duration;
    // 起播时刻（真实时钟）与流位置：端到端延迟 = at - (流建立时刻 + streamMs)
    stats.schedLog.push({
      streamMs: streamBytes / frameBytes / rate * 1000,
      at: ctxEpoch + nextStart
    });
    if (stats.schedLog.length > 40) stats.schedLog.shift();
    streamBytes += f32.length * frameBytes;
    var bms = Math.round((nextStart - now) * 1000);
    if (bms > stats.maxBufMs) stats.maxBufMs = bms;
    stats.blocks++;
  }

  function onBytes(u8) {
    var data;
    if (tail && tail.length) {
      data = new Uint8Array(tail.length + u8.length);
      data.set(tail, 0);
      data.set(u8, tail.length);
    } else data = u8;

    var off = 0;
    if (!wavOk) {
      if (data.length < 44) { tail = data; return; }
      // 44 字节标准 PCM 头（服务端 AudioForwarder.BuildWavHeader 逐字一致）
      var dv = new DataView(data.buffer, data.byteOffset);
      var fmt = dv.getUint16(20, true);
      channels = dv.getUint16(22, true);
      rate = dv.getUint32(24, true);
      bits = dv.getUint16(34, true);
      isFloat = (fmt === 3);
      if (channels < 1 || channels > 8 || rate < 8000 || rate > 192000
          || (bits !== 8 && bits !== 16 && bits !== 24 && bits !== 32)) {
        stats.badFmt++;
        if (curReader) { try { curReader.cancel(); } catch (e) { } }
        backoff();
        return;
      }
      frameBytes = channels * (bits / 8);
      wavOk = true;
      off = 44;
    }

    var avail = data.length - off;
    var usable = avail - (avail % frameBytes);        // 帧对齐
    var blockFrames = Math.max(1, Math.round(rate * BLOCK_MS / 1000));
    var blockBytes = blockFrames * frameBytes;
    var pos = off, end = off + usable;
    while (pos + blockBytes <= end) {
      schedule(toFloat32(data, pos, blockFrames));
      pos += blockBytes;
    }
    tail = data.slice(pos);   // 残余留给下一个 read 块拼齐
  }

  function flushTail() {
    // 连接结束：把不足一块的残余也播掉（话音末尾丢掉就是漏词）
    if (wavOk && tail && tail.length >= frameBytes) {
      var frames = Math.floor(tail.length / frameBytes);
      schedule(toFloat32(tail, 0, frames));
    }
    tail = null;
  }

  function backoff() {
    retry++;
    setTimeout(attempt, Math.min(1000 * retry, 8000));
  }

  // ---- 回退路径：<audio> 元素（旧方案，保底出声，延迟 ~2.2s）----
  function attemptAudioEl() {
    if (!audEl) {
      audEl = document.createElement('audio');
      audEl.id = 'audfb';
      audEl.setAttribute('playsinline', '');
      audEl.autoplay = true;
      audEl.style.display = 'none';
      document.body.appendChild(audEl);
      audEl.addEventListener('playing', function () { audRetry = 0; audEverOk = true; });
      audEl.addEventListener('error', function () {
        if (audEverOk) return;   // 已经正常过就别重连
        audRetry++;
        var wait = Math.min(1000 * audRetry, 8000);
        setTimeout(function () {
          if (!audEl) return;
          // 换 src 前先彻底清干净：暂停 + 断开 + 清空 src，
          // 否则旧的流式连接还占着一条同源槽位（同源只有 6 条，
          // 被占满会让所有 fetch 含打字排队）。
          try {
            audEl.pause();
            audEl.removeAttribute('src');
            audEl.load();          // 强制中断当前流式请求，释放连接
          } catch (e) { /* 忽略 */ }
          audEl.src = '/audio?t=' + Date.now();
          audEl.play && audEl.play().catch(function () { });
        }, wait);
      });
    }
    audEl.src = '/audio?t=' + Date.now();
    var p = audEl.play();
    if (p && p.catch) p.catch(function () {
      // ★无手势时浏览器拒绝 play（不触发 error 事件，连接照连、数据照收，
      //   就是不出声——最隐蔽的「没声音」形态）。一次性提示，不刷屏。
      if (!ctxHinted) {
        ctxHinted = true;
        hint('浏览器拦截了自动播放：点一下画面即可开声');
      }
    });
  }

  function switchToAudioEl(reason) {
    if (mode === 'audio-element') return;
    mode = 'audio-element';
    fallbackReason = reason;
    keepBuf = []; keepPending = false;   // 回退路径不管保留内容，清掉防脏状态
    // ★先淡出挂起 Web Audio（round27）：原来只 cancel reader，ctx 里的已排队
    //   源还在跑 → audio 元素起来后两路同时播 = 重叠爆音（回退切换的「嗒」）。
    fadeOut('suspend');
    // 断开可能还在走的 Web Audio 流，避免服务端继续往一个没人读的连接推数据
    if (curReader) { try { curReader.cancel(); } catch (e) { } }
    hint('已切换兼容播放模式（声音约晚 2 秒，保底出声）。'
      + '若仍无声：点一下画面，并检查手机侧边静音开关。');
    attemptAudioEl();
  }

  function switchToWebAudio() {
    if (mode === 'webaudio') return;
    mode = 'webaudio';
    fallbackReason = '';
    if (audEl) {
      try { audEl.pause(); audEl.removeAttribute('src'); audEl.load(); } catch (e) { }
    }
    // 重新走一遍连接（wavOk 等状态沿用，块继续接播）
    if (!pumping) { pumping = true; attempt(); }
  }

  // ---- 延迟测量（2026-10-15 加，round26 修正口径：先测量再调参）----
  // ★round26 口径修正：原来把 e2eMs 直接当「端到端」是错的——它算的是
  //   「最新调度的块」落后实况多少，而实时流的块边到边排，这个数天然≈0
  //   （20:41 实测健康时它显示 20ms，用户听到的却是 763ms 以前的音频）。
  //   正确口径：**感知延迟 = 流入滞后 + 缓冲深度 + 输出延迟**。
  //   流入滞后 = 当前时刻 -（连接建立时刻 + 已调度音频时长）
  //           = 网络+服务端节流滞后（断流重连/挂起丢弃会让它涨）。
  function e2eMs() {
    if (!stats.connAt || !streamBytes) return -1;
    var nowSec = performance.now() / 1000;
    var playedSec = streamBytes / frameBytes / rate;
    var ms = (nowSec - stats.connAt - playedSec) * 1000;
    return ms > -5000 && ms < 60000 ? Math.round(ms) : -1;
  }
  // 感知延迟（ms）= 流入滞后 + 已排未播深度 + 声卡输出延迟。
  // 这就是用户耳朵与 PC 实况之间的真实差距；调 tgt/keep 前先看它。
  function perceivedMs() {
    var e = e2eMs(), d = bufDepthMs(), o = outLatMs();
    if (e < 0) return -1;
    var v = e + (d > 0 ? d : 0) + (o > 0 ? o : 0);
    return v > -5000 && v < 60000 ? v : -1;
  }
  // AudioContext 实际输出延迟（验证 latencyHint:'interactive' 生没生效）：
  //   Chrome/Edge/Android Chrome 107+ 有 outputLatency；Safari 只有 baseLatency；
  //   都没有返回 -1（显示 ?）。
  function outLatMs() {
    if (!ctx) return -1;
    if (typeof ctx.outputLatency === 'number' && ctx.outputLatency > 0) return Math.round(ctx.outputLatency * 1000);
    if (typeof ctx.baseLatency === 'number' && ctx.baseLatency > 0) return Math.round(ctx.baseLatency * 1000);
    return -1;
  }
  // 已排未播的缓冲深度（ms）：nextStart - currentTime。
  function bufDepthMs() {
    if (!ctx || !nextStart) return -1;
    return Math.max(0, Math.round((nextStart - ctx.currentTime) * 1000));
  }

  // ---- 诊断条（?diag=1 显示；用户截图即可自证，不用进控制台）----
  // ★教训（2026-10-14「没声音」三轮定位）：让普通用户开手机浏览器控制台
  //   不现实，客观指标必须能「看见」。半透明小条不挡画面，UI 精简不受影响。
  var diagTimer = null;
  function diagLine() {
    var e2e = e2eMs(), out = outLatMs(), dep = bufDepthMs(), per = perceivedMs();
    return '声音[' + mode + '] ctx=' + (ctx ? ctx.state : 'none')
      // ★感知放第一位（round26）：这才是用户听到的延迟；流入/缓冲/输出是它的分解
      + ' 感知' + (per >= 0 ? '≈' + per + 'ms' : '≈?')
      + ' 流入' + (e2e >= 0 ? e2e + 'ms' : '?')
      + ' 输出' + (out >= 0 ? out + 'ms' : '?')
      // round27c：没有 outputLatency 的老设备退回 baseLatency，标出来源
      + (ctx && typeof ctx.outputLatency !== 'number' ? '(bl)' : '')
      // ★round27c 新增两字段——「呲呲呲电流声 + 原声消失」故障判据：
      //   sr = ctx 采样率。≠48000 时浏览器在重采样我们的 48k buffer，
      //      设备切换后ctx重建为44.1k是已知噪声场景（截图输出2584ms）。
      //   挂N = ctx 被系统挂起的次数。涨得凶 = 音频焦点在被反复抢夺，
      //      每次恢复都是输出链路重建的机会（也是异常态的机会）。
      + ' sr' + (ctx ? ctx.sampleRate : '?')
      + (stats.ctxSuspendCount > 0 ? ' 挂' + stats.ctxSuspendCount : '')
      // ★round28 诊断字段：「呲呲呲」故障的直接读数与自愈动作留痕。
      //   出异Nms = outputLatency 已连续异常多久（涨到 5000 就动手）；
      //   出自愈N = 因 outputLatency 自动重建了几次（>0 且仍呲呲 = 假设
      //   需要修正，截图即可判断）。
      + (outLatBadSince > 0 ? ' 出异' + Math.round(performance.now() - outLatBadSince) + 'ms' : '')
      + (outLatRebuilds > 0 ? ' 出自愈' + outLatRebuilds : '')
      + ' 缓冲' + (dep >= 0 ? dep + 'ms' : '?')
      + ' 块' + stats.blocks
      + (stats.droppedMs > 0 ? '(丢' + Math.round(stats.droppedMs / 1000) + 's)' : '')
      + ' 读' + stats.reads
      + ' 字节' + (stats.bytes / 1024).toFixed(0) + 'K'
      + ' 跳' + stats.jumps + ' 坏格式' + stats.badFmt
      + ' 流失败' + streamFails
      // ★round24 新增三字段（10-15「没声音」复盘：原有字段分不出
      //   「服务端没了」和「僵尸 ctx」两种静默，只能靠用户反复刷新试）：
      //   时钟 = ctx.currentTime 实时值，每 500ms 刷新一次；两次读数不变
      //         = 渲染时钟冻结（僵尸）；'(冻)' = 我们的检测器也认定冻结
      //   流   = 距上次读到字节的毫秒数；>2000 且连接还在 = 流停/服务端断
      //   连接 = 本次连接年龄（配合「流」判断是「从来没连上」还是「断了」）
      + ' 时钟' + (ctx ? ctx.currentTime.toFixed(1) : '?') + (clockFrozenMs > 500 ? '(冻)' : '')
      + ' 流' + (stats.lastReadAt ? Math.max(0, Date.now() - stats.lastReadAt) + 'ms' : '-')
      + ' 连接' + (stats.connAt ? Math.round(performance.now() / 1000 - stats.connAt) + 's' : '-')
      + (retry > 0 ? ' 重试' + retry : '')
      + (ctxRebuilds > 0 ? ' 重建' + ctxRebuilds : '')
      // 当前生效的调参值（URL 可覆盖）：A/B 时截图自证用的是哪组参数
      + ' tgt' + TARGET_MS + ' keep' + KEEP_MS
      // round27 淡入淡出计数：刷新一次 fadeOuts+1、起播一次 fadeIns+1；
      // 两个都恒 0 = 渐变没接入（老浏览器或 fade=0）；fadeOuts 远大于
      // fadeIns = 页面在被反复销毁（刷新/切后台）而没起来播
      + ' 淡入' + stats.fadeIns + ' 淡出' + stats.fadeOuts
      + (fallbackReason ? ' (' + fallbackReason + ')' : '');
  }
  function startDiag() {
    if (diagTimer || !/[?&]diag=1\b/.test(location.search)) return;
    var el = document.createElement('div');
    el.id = 'audi-diag';
    el.style.cssText = 'position:fixed;left:4px;top:4px;z-index:9999;'
      + 'background:rgba(0,0,0,.72);color:#0f0;font:11px/1.5 monospace;'
      + 'padding:4px 6px;border-radius:4px;pointer-events:none;'
      + 'max-width:60vw;white-space:pre-wrap;word-break:break-all;';
    document.body.appendChild(el);
    var upd = function () { el.textContent = diagLine(); };
    upd();
    diagTimer = setInterval(upd, 500);
  }

  function attempt() {
    if (mode === 'audio-element') { attemptAudioEl(); return; }
    // ★原来这里 ensureCtx 失败直接 return，等于永久放弃（老浏览器永远没声）
    if (!ensureCtx()) { backoff(); return; }
    resume();
    fetch('/audio?t=' + Date.now())
      .then(function (res) {
        if (res.status === 503 || !res.ok) {
          // ★10-15 用户实测复盘：游戏退出后页面静默重试，用户对着静止画面
          //   完全不知道「电脑端没了」。反馈层必须无条件挂——服务挂掉的
          //   那一刻恰恰最需要提示（本项目用教训换来的原则）。
          netHint('电脑端音频服务未就绪，正在重试（需保持游戏运行）。');
          backoff(); return null;
        }
        // ★流式读取能力检测：iOS Safari <16.4 的 res.body 是 undefined；
        //   部分 webview 有 body 但没有 getReader。两种情况都只能回退。
        if (!res.body || typeof res.body.getReader !== 'function') {
          switchToAudioEl('no-stream-reader');
          return null;
        }
        var reader;
        try { reader = res.body.getReader(); }
        catch (e) {
          switchToAudioEl('getreader-throw');
          return null;
        }
        retry = 0;
        streamFails = 0;
        everOk = true;
        curReader = reader;
        // ★每次新连接都重置测量基准（round27c）：原来 connAt 只设一次、
        //   streamBytes 跨连接累积，重连+补播+挂起补偿叠加后 e2e 飘到几秒
        //   外 → 诊断条「感知?」（22:40、23:03 两张截图全是 ?，测量失灵）。
        //   keepBuf 补播的旧内容算进来只有 -240ms 的一次性偏差，比飘几秒强。
        stats.connAt = performance.now() / 1000;
        streamBytes = 0;
        startDiag();
        // ★「连接成功但收不到数据」自动提示：连接稳定 + reads 不涨 =
        //   服务端没推（或网络黑洞）。以前这个状态静默，用户只能干等。
        setTimeout(function () {
          if (curReader === reader && stats.reads === 0 && mode === 'webaudio') {
            hint('音频连接已建立但收不到数据（服务端可能没在推）。'
              + '若持续如此，请把这个画面截图给开发者。');
          }
        }, 5000);
        // ★「数据到达但不播放」自动回退（2026-10-15 加，兜底 10-14 深夜那类
        //   神秘蒸发）：连接正常、reads 持续涨但 blocks 恒 0 ⇒ ctx 挂起未恢复
        //   或调度全被丢弃 → 切 <audio> 保底出声，远好过「静默没声音」。
        //   阈值 reads>200KB：正常起播前首波数据通常远小于此；8s 足够完成
        //   首块调度（TARGET_MS=120 + 网络重连）。
        setTimeout(function () {
          if (mode !== 'webaudio' || !everOk) return;
          if (stats.reads > 200000 && stats.blocks === 0) {
            switchToAudioEl('no-blocks');
          }
        }, 8000);
        var step = function () {
          return curReader.read().then(function (r) {
            if (r.done) {
              flushTail();
              netHint('音频流已断开，正在重连（若电脑端已退出游戏，声音会一直停）。');
              backoff(); return;
            }
            var v = r.value;
            if (v && v.length) {
              if (!stats.firstByteAt) stats.firstByteAt = Date.now();
              stats.reads++;
              stats.bytes += v.length;
              stats.lastReadAt = Date.now();
              stats.readLog.push({ at: Date.now(), len: v.length, total: stats.bytes });
              if (stats.readLog.length > 40) stats.readLog.shift();
              onBytes(v);
            }
            return step();
          });
        };
        // ★读取循环自己的 catch：原来只有最外层 .catch，而 step() 是链式
        //   调用，异常其实能冒泡到外层——这里显式处理并计入连续失败，
        //   偶发网络错误重试，连续 3 次才回退（Wi-Fi 抖一下不该直接回退）。
        return step().catch(function () {
          netHint('音频数据读取中断，正在重连。');
          streamFails++;
          if (streamFails >= 3) switchToAudioEl('read-fail');
          else backoff();
        });
      })
      .catch(function () {
        netHint('音频连接失败，正在重试（检查手机与电脑是否同一路由器）。');
        streamFails++;
        if (streamFails >= 3) switchToAudioEl('fetch-fail');
        else backoff();
      });
  }

  function pump() {
    if (pumping) return;
    pumping = true;
    attempt();
  }

  // 诊断接口（控制台可跑 __audioDebug.stats() / __audioDebug.bufMs() /
  // __audioDebug.path() 自证；__audioDebug.force('audio-element') 手动 A/B）；
  // 实验室 latencylab 也靠它挂 AnalyserNode 测量。
  window.__audioDebug = {
    ctx: function () { return ctx; },
    gain: function () { return gain; },
    stats: function () {
      return {
        blocks: stats.blocks, jumps: stats.jumps, maxBufMs: stats.maxBufMs,
        badFmt: stats.badFmt, firstByteAt: stats.firstByteAt,
        reads: stats.reads, bytes: stats.bytes,
        droppedMs: stats.droppedMs,
        lastReadAt: stats.lastReadAt,
        ctxRebuilds: ctxRebuilds,
        clockFrozenMs: clockFrozenMs,
        keepMs: keepBuf.reduce(function (a, b) { return a + b.length / rate * 1000; }, 0),
        keepPending: keepPending,
        rate: rate, channels: channels, bits: bits, isFloat: isFloat,
        wavOk: wavOk,         everOk: everOk, retry: retry,
        readLog: stats.readLog.slice(),
        schedLog: stats.schedLog.slice(),
        ctxEpoch: ctxEpoch, streamBytes: streamBytes
      };
    },
    bufMs: function () { return ctx ? Math.round((nextStart - ctx.currentTime) * 1000) : -1; },
    path: function () {
      return {
        mode: mode, reason: fallbackReason,
        ctxState: ctx ? ctx.state : 'none',
        streamFails: streamFails,
        hasReader: !!(window.Response && window.ReadableStream),
        diag: diagLine()
      };
    },
    force: function (m) {
      if (m === 'audio-element') switchToAudioEl('manual');
      else if (m === 'webaudio') switchToWebAudio();
    },
    pump: pump
  };

  // 实验室强制回退开关：addInitScript 里 window.__audioForceFallback = true
  if (window.__audioForceFallback) {
    mode = 'audio-element';
    fallbackReason = 'lab-forced';
  }

  pump();

  // ---- 生命周期事件绑定（round27 抽成函数：刷新爆音的修复全在这里，
  //   zombtest 要逐字抽它验证，散在 IIFE 底部没法抽）----
  function bindLifecycle() {
    document.addEventListener('touchstart', function () {
      ensureCtx(); resume();
      if (audEl) { audEl.play && audEl.play().catch(function () { }); }
    }, { passive: true });
    document.addEventListener('click', function () {
      ensureCtx(); resume();
      if (audEl) { audEl.play && audEl.play().catch(function () { }); }
    });
    document.addEventListener('visibilitychange', function () {
      if (document.hidden) {
        // 切后台：淡出 + 挂起（round27）。原来 hidden 分支什么都没有——
        //   浏览器爱挂不挂、时钟冻不冻随缘；现在明确收敛：淡出防「嗒」，
        //   suspend 防僵尸、省电；恢复时 resume → fadeIn 淡入。
        fadeOut('suspend');
        if (audEl) { try { audEl.pause(); } catch (e) { } }
      } else {
        ensureCtx(); resume();
        if (audEl) { audEl.play && audEl.play().catch(function () { }); }
      }
    });
    // ★刷新/导航（round27「刷新爆音」主修）：pagehide 是刷新时可靠触发的
    //   生命周期事件（unload 在安卓 Chrome 不可靠）。淡出 ramp 已排在音频
    //   时钟上，页面若还能活 25ms 就完整淡出；立即死的至少 DAC 残余缓冲
    //   自然衰减，比裸切强。
    document.addEventListener('pagehide', function () {
      fadeOut('suspend');
      if (audEl) { try { audEl.pause(); } catch (e) { } }
    });
  }
  // ★刷新声音按钮（round28 保底）先绑：它改的是 DOM，与生命周期事件独立
  bindRefreshButton();
  bindLifecycle();
})();
  // ===AUDIO_PLAYER_END===

  // 声音状态不再单独跨域轮询 —— 已并入主 /status（同端口同源）。
  // ★这里只把 <audio> 的真实播放状态显示出来，不发任何额外请求。

  // ★2026-10-14：状态轮询（/status）与面板跟随（/panel）整体撤掉。
  //   撤轮询的理由：唯一用途是刷新那两行常驻状态文字，而状态行已被用户要求删除；
  //   而且每 4 秒一次的同源请求本身也在消耗 6 条连接槽位之一。
  //   撤 /panel 的理由：面板机制作废（输入框+发送常驻，不再需要唤出/隐藏）。
  //   ControlServer 的 /panel、/status 端点保留在服务端，不影响任何功能。

  // ★★ 打字：手机输入框 → POST /type → 写进游戏对话输入框。
  //
  // 【为什么不用键盘模拟】SendInput 敲不出中文 IME；直接写 TMP_InputField.text。
  //
  // 【为什么走 8894 独立端口，而不是同源 fetch / sendBeacon —— 两轮实测】
  //   第一轮（同源 fetch）：「点了没反应，关掉网页才发出去」。
  //     根因：浏览器同源并发连接上限 Chrome=6，本页常驻
  //       /mjpeg 长连接 1~2 条（永不释放）+ /audio 长连接 1 条（永不释放），
  //       fetch 排队等一个永不释放的槽位。关网页时长流释放，排队的请求才被处理。
  //   第二轮（改 sendBeacon）：**依然**是「关掉网页才生效」。
  //     sendBeacon 不是独立通道，同样受该池限制，只是优先级更低 ——
  //     槽位满时被压进「页面卸载才 flush」的队列。后端日志能看见请求最终到达，
  //     所以极易误判成「服务端慢」。
  //   ⇒ 真正的解法是**换 origin**：连接池按 (协议,主机,端口) 分组，
  //     8894 有自己完整的 6 条槽位，8895 上的长流再忙也挤不占。
  var tin = document.getElementById('tin');

  function sendType(send) {
    if (!tin) return;
    var text = (tin.value || '').replace(/^\s+|\s+$/g, '');
    if (!text) {
      hint('请先输入内容');
      return;
    }
    typeCount++;
    hint('发送中… #' + typeCount);

    // ★走 fetch 而非 sendBeacon：8894 槽位空闲，能立刻拿到 HTTP 响应做回显。
    fetch(CTRL + '/type', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: text, send: !!send }),
      cache: 'no-store'
    }).then(function (r) {
      hint('已发送 #' + typeCount + '（' + r.status + '）：' + text.slice(0, 20));
      if (send) tin.value = '';
    }).catch(function (e) {
      // ★失败信息要能区分「网络/CORS 断了」与「服务端返回了错误」——
      //   fetch 只有网络层失败才走 catch；HTTP 4xx/5xx 是正常 resolve。
      //   TypeError: Failed to fetch 在跨源场景下最常见的原因是 CORS 预检被拒。
      hint('发送失败 ' + e.message
           + '（若提示 Failed to fetch，多半是 8894 的 CORS 预检未放行）');
    });
  }

  // 回车 = 发送（手机上 enterkeyhint=send 已让键盘显示『发送』键）
  if (tin) {
    tin.addEventListener('keydown', function (e) {
      // keyCode 13 = Enter/回车。用 keyCode 而非 e.key：
      //   Android 中文输入法回车时 e.key 可能是 'Process'/'Unidentified'。
      if (e.keyCode === 13) {
        e.preventDefault();
        sendType(true);
      }
    });
    // ★点输入框时别让它触发画面点击透传（按钮栏与画面是兄弟节点，
    //   正常不会冒泡过去，但 input 的 focus 在某些浏览器会连带触发手势事件）
    tin.addEventListener('pointerdown', function (e) { e.stopPropagation(); });
  }

  // ★★ 手机麦克风语音上行（2026-10-14）
  //
  // 【交互】点「语音」按钮 = 远程点**游戏内的麦克风按钮**（2026-10-14 重设计）。
  //   语音走 PC 麦克风，进 MOD 已有 ASR（与游戏内麦克风同一条流水线）：
  //   识别 → 草稿上屏 → AsrSendGate 自动发送。手机不录音、不要麦克风权限。
  //   ★为什么不再用 getUserMedia：http://192.168.x.x 不是安全上下文，
  //     navigator.mediaDevices 整个不存在（本机 Edge 实测），安卓 Chrome 要改
  //     chrome://flags、iPhone Safari 无解——遥控游戏内麦克风没这些限制。
  //
  // 【协议】POST http://PC:8894/voice?cmd=toggle（与 /type 同 origin，无预检问题）
  //   → {""ok"":true,""result"":""started""}  开始（PC 麦克风录音中）
  //   → {""ok"":true,""result"":""stopped""}  停止，PC 侧收尾识别
  //   → {""ok"":false,""result"":""asr-unavailable:…""|""dialogue-not-open""|…} 拒绝原因
  //   录音中每 500ms 轮询 /status（8895 同源）的 asrRecording：
  //   PC 侧状态变化（游戏内按钮/ESC/60s 上限）必须同步回手机按钮。
  var micBtn = document.getElementById('mic');
  var micState = 'idle';      // idle | recording | finalizing
  var micBusy = false;        // toggle 请求在飞则忽略重复点击（防重入）
  var micSec = 0;
  var micPoll = 0;
  var MIC_MAX_SEC = 60;       // 与 MOD 侧 ASR 会话 60 秒自动停一致

  function micReset() {
    if (micPoll) { clearInterval(micPoll); micPoll = 0; }
    micState = 'idle'; micSec = 0;
    micBtn.textContent = '语音';
    micBtn.classList.remove('rec');
  }

  // 用 /status 的 ASR 字段同步按钮；返回是否仍在语音会话中（还要继续轮询）。
  function micApplyStatus(j) {
    var a = j && j.asrRecording !== undefined ? j : null;
    if (!a) return micState !== 'idle';
    if (a.asrRecording) {
      if (micState === 'idle') {   // PC 侧（游戏内按钮）先开录了 → 手机跟上
        micState = 'recording'; micSec = 0;
        micBtn.classList.add('rec');
      }
      micSec++;
      micBtn.textContent = micSec + 's';
      if (micSec >= MIC_MAX_SEC) hint('已达 60 秒上限，MOD 将自动停止识别');
      return true;
    }
    if (micState !== 'idle' && (a.asrState === '收尾中' || a.asrState === '准备中')) {
      micBtn.textContent = '识别中';
      return true;
    }
    if (micState !== 'idle') {
      micReset();
      hint('识别完成，已自动发送');
    }
    return false;
  }

  function micStartPoll() {
    if (micPoll) return;
    micPoll = setInterval(function () {
      fetch(location.origin + '/status', { cache: 'no-store' })
        .then(function (r) { return r.json(); })
        .then(function (j) { if (!micApplyStatus(j)) micReset(); })
        .catch(function () {});
    }, 500);
  }

  function micToggle() {
    if (micBusy || micState === 'finalizing') return;   // 防重入
    micBusy = true;
    if (micState === 'recording') {   // 先本地进「识别中」，等 PC 侧收尾
      micState = 'finalizing';
      micBtn.textContent = '识别中';
    }
    fetch(CTRL + '/voice?cmd=toggle', { method: 'POST', cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (j) {
        micBusy = false;
        if (!j || j.ok !== true) {
          hint('MOD 拒绝切换语音：' + (j && j.result ? j.result : '未知'));
          micReset();
          return;
        }
        if (j.result === 'started') {
          micState = 'recording'; micSec = 0;
          micBtn.textContent = '0s';
          micBtn.classList.add('rec');
          micStartPoll();
          hint('开始说话（PC 麦克风），再点一次停止并识别');
        } else {
          // stopped / canceled-warmup：轮询（录音时已启动）会等 PC 侧收尾完成后复位
          // ★2026-10-15：停止即自动发送，不用再回游戏点发送
          hint('已停止，识别完成后自动发送');
        }
      })
      .catch(function (e) {
        micBusy = false;
        hint('请求失败: ' + (e && e.message ? e.message : e));
        micReset();
      });
  }

  // 页面打开时同步一次 PC 侧状态（用户可能先用游戏内按钮开了麦克风）。
  fetch(location.origin + '/status', { cache: 'no-store' })
    .then(function (r) { return r.json(); })
    .then(function (j) {
      if (j && j.asrRecording) {
        micState = 'recording';
        micBtn.classList.add('rec');
        micStartPoll();
        hint('游戏内麦克风正在录音（PC 麦克风），点「语音」可停止');
      }
    })
    .catch(function () {});

  if (micBtn) micBtn.addEventListener('click', micToggle);

  // ★★ 本地发送计数：只用于气泡回显的编号，让用户看出「点了没点、发没发出去」。
  var typeCount = 0;

  // ★★把内联 onclick 用到的函数挂到 window。
  //
  // 【为什么必须挂 —— 实测踩坑：用户点「发送」按钮毫无反应】
  //   整段 JS 都在 IIFE `(function(){...})()` 里，函数是**私有作用域**。
  //   HTML里的 `onclick=sendType(true)` 属于内联事件处理器，
  //   它在**全局作用域**里查sendType —— 私有函数根本查不到 →
  //   抛 ReferenceError，按钮看起来『完全没反应』。
  //   （诊断按钮之前能用，只是因为只有它被显式挂到 window 上。）
  //   ⇒ 页面里每个 onclick 调用的函数都必须在这里导出。
  window.sendType = sendType;

  // 阻止双指缩放/双击放大，避免误触打乱画面
  document.addEventListener('gesturestart', function (e) { e.preventDefault(); });
  var lastTouch = 0;
  document.addEventListener('touchend', function (e) {
    // ★★ 必须放行输入框：全局『300ms 内第二次 touch 就 preventDefault』是为了
    //   防双击缩放，但它会**吞掉输入框的双击选词/双击标点**，让中文输入法很难用。
    //   判据一：事件目标在 <input>/<textarea> 里就完全不干预。
    // ★★判据三（2026-10-14 语音按钮）：BUTTON 也要放行 —— 麦克风按钮
    //   「点开始→马上点停止」是合法操作，被防双击吞掉第二次点击会让麦克风
    //   一直录到 60 秒上限。按钮在底部栏、不在画面上，没有防双击缩放的需求。
    var t = e.target;
    if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'BUTTON')) return;
    // ★★ 判据二（捏合必需）：**抬起时屏幕上还有别的手指**说明这是一次捏合，
    //   不是双击缩放。这里若也 preventDefault，会连带干掉浏览器在
    //   touchend 上的默认行为 → 表现为「捏合时画面卡住/不跟手」。
    //   （e.touches 是本次 touchend 之后仍留在屏幕上的触点）
    if (e.touches && e.touches.length > 0) return;
    var now = Date.now();
    if (now - lastTouch < 300) { e.preventDefault(); }
    lastTouch = now;
  }, { passive: false });
})();
</script>
</body>
</html>";
    }
}