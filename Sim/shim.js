/* Offline shim: answers the workstation's /api/* calls from the simulated world, so the very same page works
   without the game. Also drives the simulation clock. Needs engine.js and world.js loaded first. */
(function (root) {
  'use strict';
  var realFetch = root.fetch ? root.fetch.bind(root) : null;
  var bench = { world: null, plc: null, ladder: '', listeners: [], scenario: 'lightbutton' };
  var LIBSRC = root.PLC_LIB || '';

  function load(id) {
    var src = bench.plc ? bench.plc.source : null;
    bench.world = new SimWorld.World(id);
    bench.plc = new StSim.Plc(bench.world.store, LIBSRC);
    if (src) bench.plc.install(src);
    bench.scenario = id;
    bench.listeners.forEach(function (f) { try { f(); } catch (e) {} });
  }
  bench.load = load;
  bench.onChange = function (f) { bench.listeners.push(f); };

  function resp(body, status, type) {
    status = status || 200;
    return Promise.resolve({
      ok: status >= 200 && status < 300, status: status,
      json: function () { return Promise.resolve(JSON.parse(body)); },
      text: function () { return Promise.resolve(body); }
    });
  }
  function q(url, k) { var m = new RegExp('[?&]' + k + '=([^&]*)').exec(url); return m ? decodeURIComponent(m[1]) : null; }

  root.fetch = function (url, opt) {
    url = String(url);
    if (url.indexOf('/api/') !== 0) return realFetch ? realFetch(url, opt) : Promise.reject(new Error('offline'));
    var post = opt && opt.method === 'POST', body = opt && opt.body, p = url.split('?')[0], plc = bench.plc, w = bench.world;
    if (!post) {
      if (p === '/api/state') return resp(JSON.stringify(plc.stateJson('SIM-')));
      if (p === '/api/points') return resp(JSON.stringify(w.pointsJson()));
      if (p === '/api/program') return resp(plc.source);
      if (p === '/api/ladder') return resp(bench.ladder);
    } else {
      if (p === '/api/program') { plc.install(String(body || '')); return resp('queued'); }
      if (p === '/api/ladder') { bench.ladder = String(body || ''); try { localStorage.setItem('plcBenchLadder', bench.ladder); } catch (e) {} return resp('saved'); }
      if (p === '/api/check') return resp(JSON.stringify(plc.check(String(body || ''))));
      if (p === '/api/mode') { plc.requestMode(q(url, 'run') === '1', performance.now()); return resp('queued'); }
      if (p === '/api/prune') return resp('queued');
      if (p === '/api/force') {
        var n = q(url, 'name');
        if (q(url, 'release') === '1') plc.force(n, null); else plc.force(n, parseFloat(q(url, 'value')));
        return resp('queued');
      }
      if (p === '/api/write') {
        var a = parseInt(q(url, 'addr'), 10), v = q(url, 'value');
        if (!(a >= 0 && a < SimWorld.SIZE)) return resp('bad address', 400);
        if (plc.running && plc.blockExternal) return resp('The program is running and owns the outputs. Press Stop first.', 409);
        if (!w.store.coilAssigned[a]) return resp('That address is not an assigned switch.', 409);
        w.store.COIL[a] = v === '1';
        return resp('queued');
      }
    }
    return resp('not found', 404);
  };

  var last = performance.now();
  setInterval(function () {
    var now = performance.now(), dt = (now - last) / 1000; last = now;
    if (!bench.world) return;
    bench.world.step(dt);
    bench.plc.tick(now, 100);
  }, 50);

  try { bench.ladder = localStorage.getItem('plcBenchLadder') || ''; } catch (e) {}
  root.Bench = bench;
  load('lightbutton');
})(window);
