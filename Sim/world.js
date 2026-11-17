/* =====================================================================================================================
   Practice Bench: simulated Stationeers devices and practice scenarios.

   A World owns the Modbus-style tables (the same four the real mod exposes: DI, COIL, IR, HR), a list of virtual
   devices, and a goal checker. Devices publish points exactly like real ones, so a program written here uses the
   same %IX / %QX / %IW / %QW addresses idea as in the game. Physics is deliberately simple but behaves the way the
   real things do: doors take time to move, pumps need time to change a pressure, a locked door ignores commands.
   No browser dependencies, so it can be tested with node.
   ===================================================================================================================== */
(function (root) {
  'use strict';

  var SIZE = 512;

  function World(scenarioId) {
    var self = this;
    function bools() { var a = []; for (var i = 0; i < SIZE; i++) a.push(false); return a; }
    function nums() { var a = []; for (var i = 0; i < SIZE; i++) a.push(0); return a; }
    this.store = { DI: bools(), COIL: bools(), IR: nums(), HR: nums(), coilAssigned: bools(), hrAssigned: bools() };
    this.next = { DI: 0, COIL: 0, IR: 0, HR: 0 };
    this.points = []; this.devices = []; this.time = 0; this.log = [];
    this.faults = {};            // named switches the learner can flip on the bench (leak, pump failure, ...)
    this.ui = {};                // live values the scene needs (button states, sliders)
    var sc = SCENARIOS[scenarioId] || SCENARIOS.lightbutton;
    this.scenario = sc; this.id = sc.id;
    sc.build(this);
    this.goals = sc.goals.map(function (g) { return { id: g.id, text: g.text, done: false, bonus: !!g.bonus }; });
    this.stats = {};
    if (sc.init) sc.init(this);
  }
  var W = World.prototype;

  W.addPoint = function (t, tag, dev, sig, scale) {
    var a = this.next[t]++;
    var p = { t: t, a: a, tag: tag, dev: dev, sig: sig, scale: scale || 1 };
    this.points.push(p);
    if (t === 'COIL') this.store.coilAssigned[a] = true;
    if (t === 'HR') this.store.hrAssigned[a] = true;
    return p;
  };
  /* Device helpers. Each returns the point so the device can read its command later. */
  W.device = function (tag, dev, step) {
    var w = this, d = { tag: tag, dev: dev, w: w, step: step || function () {} };
    d.di = function (sig, fn) { var p = w.addPoint('DI', tag, dev, sig); p.fn = fn; return p; };
    d.coil = function (sig, init) { var p = w.addPoint('COIL', tag, dev, sig); w.store.COIL[p.a] = !!init; return p; };
    d.ir = function (sig, fn, scale) { var p = w.addPoint('IR', tag, dev, sig, scale); p.fn = fn; return p; };
    d.hr = function (sig, init, scale) { var p = w.addPoint('HR', tag, dev, sig, scale); w.store.HR[p.a] = Math.round((init || 0) * (scale || 1)); return p; };
    this.devices.push(d);
    return d;
  };
  W.cmd = function (p) { return this.store.COIL[p.a]; };
  W.hrv = function (p) { return this.store.HR[p.a] / p.scale; };

  W.step = function (dt) {
    dt = Math.min(dt, 0.25);
    this.time += dt;
    var i, p;
    for (i = 0; i < this.devices.length; i++) this.devices[i].step(dt, this);
    for (i = 0; i < this.points.length; i++) {
      p = this.points[i];
      if (p.t === 'DI') this.store.DI[p.a] = !!p.fn();
      else if (p.t === 'IR') this.store.IR[p.a] = Math.round(p.fn() * p.scale);
    }
    if (this.scenario.check) this.scenario.check(this, dt);
  };

  W.pointsJson = function () {
    var s = this.store, out = [];
    for (var i = 0; i < this.points.length; i++) {
      var p = this.points[i], raw, eng;
      if (p.t === 'DI') { raw = s.DI[p.a] ? 1 : 0; eng = raw; }
      else if (p.t === 'COIL') { raw = s.COIL[p.a] ? 1 : 0; eng = raw; }
      else { raw = p.t === 'IR' ? s.IR[p.a] : s.HR[p.a]; eng = p.scale === 1 ? raw : (raw / p.scale); }
      out.push({ t: p.t, a: p.a, tag: p.tag, dev: p.dev, sig: p.sig, raw: raw, eng: String(parseFloat(Number(eng).toFixed(2))), act: true });
    }
    return out;
  };
  W.note = function (msg) { this.log.push({ t: this.time, msg: msg }); if (this.log.length > 30) this.log.shift(); };
  W.goal = function (id) { for (var i = 0; i < this.goals.length; i++) if (this.goals[i].id === id) return this.goals[i]; return null; };
  W.achieve = function (id) { var g = this.goal(id); if (g && !g.done) { g.done = true; this.note('Goal reached: ' + g.text); } };
  W.allDone = function () { return this.goals.every(function (g) { return g.done || g.bonus; }); };

  function approach(cur, target, rate, dt) { // move cur toward target by at most rate*dt
    var d = target - cur, m = rate * dt;
    return Math.abs(d) <= m ? target : cur + (d > 0 ? m : -m);
  }
  function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }

  /* ------------------------------------------------------------------ device builders */
  function addLight(w, tag) {
    var d = w.device(tag, 'Wall Light'), on = null;
    d.state = { glow: 0 };
    on = d.coil('On', false);
    d.di('Power', function () { return true; });
    d.di('On', function () { return w.cmd(on); });
    d.step = function (dt) { d.state.glow = approach(d.state.glow, w.cmd(on) ? 1 : 0, 6, dt); };
    d.on = function () { return w.cmd(on); };
    return d;
  }
  function addButton(w, tag, uiKey) {
    var d = w.device(tag, 'Logic Button');
    w.ui[uiKey] = false;
    d.di('Power', function () { return true; });
    d.di('Activate', function () { return w.ui[uiKey]; });
    return d;
  }
  function addDoor(w, tag) {
    var d = w.device(tag, 'Door'), open = d.coil('Open', false), lock = d.coil('Lock', false);
    d.state = { pos: 0 };            // 0 closed .. 1 open
    d.lockCmd = lock;
    d.di('Power', function () { return true; });
    d.di('Open', function () { return d.state.pos > 0.5; });
    d.di('Lock', function () { return w.cmd(lock); });
    d.ir('Position', function () { return Math.round(d.state.pos * 100); });
    // A locked door can still be closed, but it will not open: that is the interlock the lock is for.
    d.step = function (dt) {
      var target = w.cmd(open) ? 1 : 0;
      if (w.cmd(lock) && target > d.state.pos) target = d.state.pos;
      d.state.pos = approach(d.state.pos, target, 0.5, dt);
    };
    d.isOpen = function () { return d.state.pos > 0.2; };
    return d;
  }

  /* ------------------------------------------------------------------ scenarios */
  var SCENARIOS = {};

  SCENARIOS.lightbutton = {
    id: 'lightbutton', title: 'Light and button',
    blurb: 'A wall light and a push button. The simplest PLC job: read an input, drive an output.',
    task: 'Make the button control the light. Then try the bonus goals.',
    hint: 'Use a contact on the button’s Activate signal and a coil on the light’s On switch. For a toggle you will need a latch (SR block) or an edge (R_TRIG).',
    build: function (w) { w.light = addLight(w, 'SIM-Light'); w.button = addButton(w, 'SIM-Button', 'btn'); },
    goals: [
      { id: 'held', text: 'Light is ON while the button is held' },
      { id: 'released', text: 'Light goes OFF when the button is released' },
      { id: 'latch', bonus: true, text: 'Bonus: light STAYS on after release (a toggle or latch)' },
      { id: 'second', bonus: true, text: 'Bonus: the next press turns it OFF again' }
    ],
    init: function (w) { w.stats = { prev: false, presses: 0, settle: -1, firstOn: false }; },
    check: function (w, dt) {
      // The PLC only scans every 0.1 s, so judge the light a moment AFTER the button changes.
      var s = w.stats, b = w.ui.btn, on = w.light.on();
      if (b && on) w.achieve('held');
      if (b && !s.prev) s.presses++;
      if (!b && s.prev) s.settle = 0.6;
      if (s.settle >= 0) {
        s.settle -= dt;
        if (b) s.settle = -1;
        else if (s.settle < 0) {
          if (s.presses === 1) { if (on) { w.achieve('latch'); s.firstOn = true; } else w.achieve('released'); }
          else if (s.firstOn && !on) w.achieve('second');
        }
      }
      s.prev = b;
    }
  };

  SCENARIOS.beacon = {
    id: 'beacon', title: 'Warning beacon',
    blurb: 'A warning light that should flash. Practice timers, or use the BLINK block.',
    task: 'Make the light flash about once per second, with the light on roughly half of the time.',
    hint: 'The BLINK block does this in one step. Or build it with a TON timer that restarts itself.',
    build: function (w) { w.light = addLight(w, 'SIM-Beacon'); },
    goals: [
      { id: 'flash', text: 'Light switches on and off at least 6 times' },
      { id: 'duty', text: 'The light is on for between 30% and 70% of each flash' }
    ],
    init: function (w) { w.stats = { prev: false, last: 0, edges: 0, onT: 0, offT: 0, okCycles: 0 }; },
    check: function (w, dt) {
      var s = w.stats, on = w.light.on();
      if (on) s.onT += dt; else s.offT += dt;
      if (on !== s.prev) {
        s.edges++;
        if (on) {   // a flash cycle (on then off) just completed: judge how long the light was on
          var tot = s.onT + s.offT;
          if (tot > 0.4 && s.onT / tot > 0.3 && s.onT / tot < 0.7) s.okCycles++;
          s.onT = 0; s.offT = 0;
        }
        s.prev = on;
      }
      if (s.edges >= 6) w.achieve('flash');
      if (s.okCycles >= 3) w.achieve('duty');
    }
  };

  SCENARIOS.tank = {
    id: 'tank', title: 'Tank pump',
    blurb: 'A gas tank is being drawn down by consumers. A pump refills it. Keep the pressure in a safe band without wearing the pump out.',
    task: 'Keep the tank between 100 and 300 kPa for 60 seconds. The pump may not switch on and off more than 8 times in that minute.',
    hint: 'Switching at a single value makes the pump chatter. Use two limits with a gap: a HYST block, or Set/Reset coils. Try raising the demand slider.',
    build: function (w) {
      w.tank = { p: 150, band: 0, switches: 0, lastOn: false, history: [] };
      w.ui.demand = 3;
      var pump = w.device('SIM-Pump', 'Volume Pump'), pon = pump.coil('On', false);
      pump.state = { spin: 0, speed: 0 };
      pump.di('Power', function () { return true; });
      pump.di('On', function () { return w.cmd(pon); });
      pump.step = function (dt) {
        var on = w.cmd(pon) && !w.faults.pumpFail;
        pump.state.speed = approach(pump.state.speed, on ? 1 : 0, 1.5, dt);
        pump.state.spin += pump.state.speed * dt * 12;
        w.tank.p += pump.state.speed * 9 * (1 - w.tank.p / 450) * dt;
      };
      w.pump = pump; w.pumpCmd = pon;
      var t = w.device('SIM-Tank', 'Gas Tank');
      t.di('Power', function () { return true; });
      t.ir('Pressure', function () { return w.tank.p; });
      t.ir('Level', function () { return clamp(w.tank.p / 4.5, 0, 100); });
      t.step = function (dt) {
        var demand = w.ui.demand + (w.faults.leak ? 5 : 0);
        w.tank.p = Math.max(0, w.tank.p - demand * dt);
      };
    },
    goals: [{ id: 'band', text: 'Hold 100 to 300 kPa for 60 seconds' }, { id: 'wear', text: 'Pump switches 8 times or fewer in that minute' }],
    init: function (w) { w.stats = { inBand: 0, switches: 0, lastOn: false, min: 1e9, max: 0, mark: 0 }; },
    check: function (w, dt) {
      var s = w.stats, p = w.tank.p, on = w.cmd(w.pumpCmd);
      if (on !== s.lastOn) { s.lastOn = on; s.switches++; }
      if (p >= 100 && p <= 300) s.inBand += dt; else { s.inBand = 0; s.switches = 0; }
      if (p < 20 || p > 430) w.note(p < 20 ? 'Tank nearly empty!' : 'Tank over-pressure!');
      if (s.inBand >= 60) { w.achieve('band'); if (s.switches <= 8) w.achieve('wear'); }
    }
  };

  SCENARIOS.airlock = {
    id: 'airlock', title: 'Airlock',
    blurb: 'A two-door airlock between the station (100 kPa) and space (0 kPa). Two buttons call it inside or outside. Two pumps fill or empty the chamber.',
    task: 'Cycle the airlock in and out twice. Never open both doors at once, and never open a door while the chamber pressure is far from that side.',
    hint: 'The AIRLOCK block does all of this for you. Wire its outputs to the doors, locks and pumps, and its inputs to the buttons and the chamber Pressure. Then flip the faults on the bench to test its safety timeout.',
    build: function (w) {
      w.ch = { p: 0, breach: 0, blast: 0 };
      w.ui.goIn = false; w.ui.goOut = false;
      w.doorIn = addDoor(w, 'SIM-Door-In'); w.doorOut = addDoor(w, 'SIM-Door-Out');
      var fill = w.device('SIM-FillPump', 'Volume Pump'), fon = fill.coil('On', false);
      var evac = w.device('SIM-EvacPump', 'Volume Pump'), eon = evac.coil('On', false);
      fill.state = { spin: 0, speed: 0 }; evac.state = { spin: 0, speed: 0 };
      fill.di('On', function () { return w.cmd(fon); }); evac.di('On', function () { return w.cmd(eon); });
      w.fill = fill; w.evac = evac; w.fillCmd = fon; w.evacCmd = eon;
      var ch = w.device('SIM-Chamber', 'Gas Sensor');
      ch.ir('Pressure', function () { return w.ch.p; });
      addButton(w, 'SIM-Button-In', 'goIn'); addButton(w, 'SIM-Button-Out', 'goOut');
      ch.step = function (dt) {
        var P = w.ch.p, stationP = 100, spaceP = 0, pumpsOk = !w.faults.pumpFail;
        fill.state.speed = approach(fill.state.speed, w.cmd(fon) ? 1 : 0, 2, dt); evac.state.speed = approach(evac.state.speed, w.cmd(eon) ? 1 : 0, 2, dt);
        fill.state.spin += fill.state.speed * dt * 12; evac.state.spin += evac.state.speed * dt * 12;
        if (pumpsOk) {
          if (w.cmd(fon)) P += Math.min(12 * dt * fill.state.speed, Math.max(0, stationP - P));
          if (w.cmd(eon)) P -= Math.min(12 * dt * evac.state.speed, P);
        }
        var inO = w.doorIn.isOpen(), outO = w.doorOut.isOpen();
        if (inO) P += (stationP - P) * Math.min(1, 3 * dt);
        if (outO) P += (spaceP - P) * Math.min(1, 3 * dt);
        if (!inO && !outO && w.faults.leak) P = Math.max(0, P - 3 * dt);
        w.ch.p = clamp(P, 0, 120);
      };
    },
    goals: [
      { id: 'in2', text: 'Cycle in twice (inner door opened with the chamber pressurised)' },
      { id: 'out2', text: 'Cycle out twice (outer door opened with the chamber empty)' },
      { id: 'safe', text: 'Both doors never open at the same time' },
      { id: 'blast', text: 'No door opened against a big pressure difference' }
    ],
    init: function (w) { w.stats = { inN: 0, outN: 0, prevIn: false, prevOut: false, breaches: 0, blasts: 0, both: false }; },
    check: function (w) {
      var s = w.stats, inO = w.doorIn.state.pos > 0.2, outO = w.doorOut.state.pos > 0.2, P = w.ch.p;
      if (inO && !s.prevIn) { if (Math.abs(P - 100) > 30) { s.blasts++; w.note('Inner door opened against ' + Math.round(Math.abs(P - 100)) + ' kPa!'); } else s.inN++; }
      if (outO && !s.prevOut) { if (Math.abs(P) > 30) { s.blasts++; w.note('Outer door opened against ' + Math.round(P) + ' kPa!'); } else s.outN++; }
      var both = inO && outO;
      if (both && !s.both) { s.breaches++; w.note('BREACH: both doors open!'); }
      s.both = both; s.prevIn = inO; s.prevOut = outO;
      w.ch.breach = s.breaches; w.ch.blast = s.blasts;
      if (s.inN >= 2) w.achieve('in2');
      if (s.outN >= 2) w.achieve('out2');
      var g = w.goal('safe'), b = w.goal('blast');
      if (s.inN >= 2 && s.outN >= 2) { if (s.breaches === 0) w.achieve('safe'); if (s.blasts === 0) w.achieve('blast'); }
      if (s.breaches > 0 && g) g.failed = true;
      if (s.blasts > 0 && b) b.failed = true;
    }
  };

  var ORDER = ['lightbutton', 'beacon', 'tank', 'airlock'];

  root.SimWorld = { World: World, SCENARIOS: SCENARIOS, ORDER: ORDER, SIZE: SIZE };
  if (typeof module !== 'undefined' && module.exports) module.exports = root.SimWorld;
})(typeof window !== 'undefined' ? window : globalThis);
