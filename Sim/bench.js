/* Practice tab: scene drawing and controls for the simulated scenarios. Needs shim.js (window.Bench). */
(function () {
  'use strict';
  var B = window.Bench, root = document.getElementById('practice');
  if (!B || !root) return;
  var FAULTS = { tank: [['leak', 'Leak in the tank (extra drain)'], ['pumpFail', 'Pump failure (motor will not turn)']],
                 airlock: [['leak', 'Slow leak in the sealed chamber'], ['pumpFail', 'Pump failure (pumps will not move gas)']] };
  var BUTTONS = { lightbutton: [['btn', 'Hold the push button']], airlock: [['goIn', 'Call: go INSIDE'], ['goOut', 'Call: go OUTSIDE']] };
  var svg, goalsEl, logEl, taskEl, hintEl, timer = null;

  function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  function build() {
    var w = B.world, sc = w.scenario, h = '';
    h += '<div class="pb-top"><label>Scenario <select id="pbSel">';
    SimWorld.ORDER.forEach(function (id) { h += '<option value="' + id + '"' + (id === B.scenario ? ' selected' : '') + '>' + esc(SimWorld.SCENARIOS[id].title) + '</option>'; });
    h += '</select></label><button id="pbReset" title="Stop the PLC, put the bench back to the start and clear the goals">Reset bench</button>' +
         '<span class="hint">Simulated devices. No game needed. Use the I/O list on the right to find addresses.</span></div>';
    h += '<p class="pb-blurb">' + esc(sc.blurb) + '</p><p class="pb-task"><b>Your job:</b> ' + esc(sc.task) + '</p>';
    h += '<svg id="pbSvg" viewBox="0 0 640 262" preserveAspectRatio="xMidYMid meet"></svg>';
    h += '<div class="pb-ctl">';
    (BUTTONS[sc.id] || []).forEach(function (b) { h += '<button class="pb-hold" data-ui="' + b[0] + '">' + esc(b[1]) + '</button>'; });
    if (sc.id === 'tank') h += '<label>Demand <input id="pbDemand" type="range" min="0" max="12" step="0.5" value="' + w.ui.demand + '"> <span id="pbDv">' + w.ui.demand + ' kPa/s</span></label>';
    (FAULTS[sc.id] || []).forEach(function (f) { h += '<label class="pb-f"><input type="checkbox" data-f="' + f[0] + '"' + (w.faults[f[0]] ? ' checked' : '') + '> ' + esc(f[1]) + '</label>'; });
    h += '</div><h3>Goals</h3><ul id="pbGoals"></ul><details><summary>Hint</summary><p>' + esc(sc.hint) + '</p></details><h3>Events</h3><div id="pbLog" class="pb-log"></div>';
    root.innerHTML = h;
    svg = document.getElementById('pbSvg'); goalsEl = document.getElementById('pbGoals'); logEl = document.getElementById('pbLog');
    document.getElementById('pbSel').onchange = function () { stopPlc(); B.load(this.value); };
    document.getElementById('pbReset').onclick = function () { stopPlc(); B.load(B.scenario); };
    Array.prototype.forEach.call(root.querySelectorAll('.pb-hold'), function (b) {
      var k = b.getAttribute('data-ui');
      function on(e) { e.preventDefault(); B.world.ui[k] = true; b.classList.add('down'); }
      function off() { B.world.ui[k] = false; b.classList.remove('down'); }
      b.addEventListener('mousedown', on); b.addEventListener('touchstart', on);
      b.addEventListener('mouseup', off); b.addEventListener('mouseleave', off); b.addEventListener('touchend', off);
    });
    var dm = document.getElementById('pbDemand');
    if (dm) dm.oninput = function () { B.world.ui.demand = parseFloat(this.value); document.getElementById('pbDv').textContent = this.value + ' kPa/s'; };
    Array.prototype.forEach.call(root.querySelectorAll('input[data-f]'), function (c) { c.onchange = function () { B.world.faults[c.getAttribute('data-f')] = c.checked; }; });
  }
  function stopPlc() { if (B.plc) B.plc.running = false; }

  function bulb(cx, cy, g, label) {
    var c = Math.round(60 + 195 * g);
    return '<circle cx="' + cx + '" cy="' + cy + '" r="' + (34 + 12 * g) + '" fill="rgba(255,220,80,' + (0.25 * g).toFixed(2) + ')"/>' +
      '<circle cx="' + cx + '" cy="' + cy + '" r="26" fill="rgb(' + c + ',' + c + ',' + Math.round(40 + 40 * g) + ')" stroke="#8394a8" stroke-width="3"/>' +
      '<rect x="' + (cx - 14) + '" y="' + (cy + 24) + '" width="28" height="14" fill="#5b6b80"/><text x="' + cx + '" y="' + (cy + 58) + '" text-anchor="middle" fill="#8394a8" font-size="13">' + label + '</text>';
  }
  function rotor(cx, cy, spin, label) {
    var s = '<circle cx="' + cx + '" cy="' + cy + '" r="22" fill="#223044" stroke="#60a5fa" stroke-width="2"/>';
    for (var i = 0; i < 3; i++) { var a = spin + i * 2.094; s += '<line x1="' + cx + '" y1="' + cy + '" x2="' + (cx + 19 * Math.cos(a)).toFixed(1) + '" y2="' + (cy + 19 * Math.sin(a)).toFixed(1) + '" stroke="#60a5fa" stroke-width="4" stroke-linecap="round"/>'; }
    return s + '<text x="' + cx + '" y="' + (cy + 40) + '" text-anchor="middle" fill="#8394a8" font-size="12">' + label + '</text>';
  }
  var DRAW = {
    lightbutton: function (w) {
      return bulb(190, 100, w.light.state.glow, 'SIM-Light') +
        '<circle cx="450" cy="100" r="34" fill="' + (w.ui.btn ? '#4ade80' : '#223044') + '" stroke="#8394a8" stroke-width="3"/><text x="450" y="160" text-anchor="middle" fill="#8394a8" font-size="13">SIM-Button</text>';
    },
    beacon: function (w) { return bulb(320, 100, w.light.state.glow, 'SIM-Beacon'); },
    tank: function (w) {
      var p = w.tank.p, lvl = Math.max(0, Math.min(1, p / 450)), y0 = 30, hh = 170;
      var s = '<rect x="330" y="' + y0 + '" width="120" height="' + hh + '" rx="14" fill="#10151c" stroke="#8394a8" stroke-width="3"/>' +
        '<rect x="334" y="' + (y0 + hh - hh * lvl + 2).toFixed(1) + '" width="112" height="' + Math.max(0, hh * lvl - 4).toFixed(1) + '" fill="rgba(96,165,250,.55)"/>';
      // safe band 100..300 kPa
      [100, 300].forEach(function (v) { var y = y0 + hh - hh * v / 450; s += '<line x1="322" y1="' + y.toFixed(1) + '" x2="458" y2="' + y.toFixed(1) + '" stroke="#fbbf24" stroke-dasharray="5 4"/><text x="466" y="' + (y + 4).toFixed(1) + '" fill="#fbbf24" font-size="11">' + v + '</text>'; });
      s += '<text x="390" y="' + (y0 + hh + 24) + '" text-anchor="middle" fill="#dbe5f0" font-size="14">' + p.toFixed(0) + ' kPa</text>';
      s += '<line x1="170" y1="120" x2="330" y2="120" stroke="' + (w.pump.state.speed > 0.2 && !w.faults.pumpFail ? '#4ade80' : '#5b6b80') + '" stroke-width="6"/>';
      s += rotor(150, 120, w.pump.state.spin, 'SIM-Pump' + (w.faults.pumpFail ? ' (FAILED)' : ''));
      s += '<text x="540" y="115" fill="#8394a8" font-size="12">drain</text><path d="M452 120 H520 M512 112 L522 120 L512 128" stroke="#f87171" stroke-width="3" fill="none" opacity="' + (0.3 + Math.min(0.7, (w.ui.demand + (w.faults.leak ? 5 : 0)) / 12)).toFixed(2) + '"/>';
      return s;
    },
    airlock: function (w) {
      var P = w.ch.p, a = Math.round(40 + P * 1.4);
      var s = '<rect x="10" y="40" width="130" height="150" fill="#1b2635"/><text x="75" y="120" text-anchor="middle" fill="#8394a8" font-size="13">STATION</text><text x="75" y="138" text-anchor="middle" fill="#8394a8" font-size="11">100 kPa</text>' +
        '<rect x="500" y="40" width="130" height="150" fill="#05080c"/><text x="565" y="120" text-anchor="middle" fill="#8394a8" font-size="13">SPACE</text><text x="565" y="138" text-anchor="middle" fill="#8394a8" font-size="11">0 kPa</text>' +
        '<rect x="170" y="60" width="300" height="110" fill="rgba(96,165,250,' + (P / 140).toFixed(2) + ')" stroke="#60a5fa" stroke-width="2"/>' +
        '<text x="320" y="122" text-anchor="middle" fill="#dbe5f0" font-size="18">' + P.toFixed(0) + ' kPa</text>';
      function door(x, d, label) {
        var open = d.state.pos, h = 110 * (1 - open);
        return '<rect x="' + x + '" y="60" width="14" height="110" fill="#10151c"/><rect x="' + x + '" y="60" width="14" height="' + h.toFixed(1) + '" fill="' + (d.isOpen() ? '#f87171' : '#8394a8') + '"/>' +
          '<text x="' + (x + 7) + '" y="52" text-anchor="middle" fill="#8394a8" font-size="11">' + label + (w.cmd(d.lockCmd) ? ' \u{1F512}' : '') + '</text>';
      }
      s += door(150, w.doorIn, 'IN') + door(466, w.doorOut, 'OUT');
      s += rotor(250, 205, w.fill.state.spin, 'fill') + rotor(390, 205, w.evac.state.spin, 'evac');
      if (w.ch.breach) s += '<text x="320" y="30" text-anchor="middle" fill="#f87171" font-size="16">BREACH: both doors were open</text>';
      return s;
    }
  };

  function frame() {
    if (!svg || root.style.display === 'none') return;
    var w = B.world, d = DRAW[w.id];
    if (d) svg.innerHTML = d(w);
    var h = '';
    w.goals.forEach(function (g) { h += '<li class="' + (g.done ? 'done' : (g.failed ? 'fail' : '')) + '">' + (g.done ? '✔ ' : (g.failed ? '✖ ' : '○ ')) + esc(g.text) + '</li>'; });
    if (goalsEl.innerHTML !== h) goalsEl.innerHTML = h;
    var l = w.log.slice(-8).reverse().map(function (e) { return '<div>' + e.t.toFixed(1) + ' s  ' + esc(e.msg) + '</div>'; }).join('');
    l = l || '<span class="hint">Nothing yet.</span>';
    if (logEl.innerHTML !== l) logEl.innerHTML = l;
  }

  B.onChange(function () { build(); frame(); });
  build();
  setInterval(frame, 80);
})();
