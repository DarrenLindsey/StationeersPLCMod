/* =====================================================================================================================
   Practice Bench: Structured Text engine (JavaScript port of StRuntime.cs + StLibrary.cs)

   Same language as the in-game PLC: PROGRAM / VAR, BOOL INT UINT DINT WORD REAL TIME TON, AT %IX %QX %IW %QW,
   IF / ELSIF / ELSE, WHILE, AND OR XOR NOT, + - * / MOD, comparisons, ABS MIN MAX LIMIT DT(),
   FUNCTION_BLOCK (the built-in library, or your own).  Function blocks are inlined at each call, so a block is
   ordinary ST with private variables named "instance.variable".

   This file has no browser dependencies, so it can also be tested with node.
   ===================================================================================================================== */
(function (root) {
  'use strict';

  var MAX_TOKENS = 5000, MAX_DEPTH = 64, MAX_NODES = 20000, MAX_INSTANCES = 64;
  var TYPES = { BOOL: 1, INT: 1, UINT: 1, DINT: 1, WORD: 1, REAL: 1, TIME: 1 };

  function StError(msg, line) { this.message = 'line ' + line + ': ' + msg; }

  /* ---------------------------------------------------------------- lexer */
  function lex(s) {
    var t = [], i = 0, line = 1, n = s.length;
    while (i < n) {
      var c = s[i];
      if (c === '\n') { line++; i++; continue; }
      if (/\s/.test(c)) { i++; continue; }
      if (c === '(' && s[i + 1] === '*') {
        var e = s.indexOf('*)', i + 2);
        if (e < 0) throw new StError('comment not closed', line);
        for (var k = i; k < e; k++) if (s[k] === '\n') line++;
        i = e + 2; continue;
      }
      if (c === '/' && s[i + 1] === '/') { while (i < n && s[i] !== '\n') i++; continue; }
      if (c === '%') {
        var st = i++;
        while (i < n && (/[A-Za-z0-9]/.test(s[i]) || s[i] === '.')) i++;
        t.push({ k: 'addr', x: s.substring(st, i).toUpperCase(), line: line }); continue;
      }
      if (/[A-Za-z_]/.test(c)) {
        var st2 = i;
        while (i < n && /[A-Za-z0-9_]/.test(s[i])) i++;
        var w = s.substring(st2, i);
        if ((w.toUpperCase() === 'T' || w.toUpperCase() === 'TIME') && s[i] === '#') {
          i++; var ts = i;
          while (i < n && (/[A-Za-z0-9]/.test(s[i]) || s[i] === '.')) i++;
          t.push({ k: 'time', x: s.substring(ts, i), line: line }); continue;
        }
        t.push({ k: 'id', x: w, line: line }); continue;
      }
      if (/[0-9]/.test(c)) {
        var st3 = i;
        while (i < n && (/[0-9]/.test(s[i]) || (s[i] === '.' && i + 1 < n && /[0-9]/.test(s[i + 1])))) i++;
        t.push({ k: 'num', x: s.substring(st3, i), line: line }); continue;
      }
      var two = s.substr(i, 2);
      if (two === ':=' || two === '<>' || two === '<=' || two === '>=') { t.push({ k: 'sym', x: two, line: line }); i += 2; continue; }
      if ('+-*/()<>=;:,.'.indexOf(c) >= 0) { t.push({ k: 'sym', x: c, line: line }); i++; continue; }
      throw new StError("unexpected character '" + c + "'", line);
    }
    return t;
  }

  function parseTime(s, err) {
    var total = 0, re = /([0-9.]+)([a-z]+)/g, m, lower = s.toLowerCase(), used = 0;
    while ((m = re.exec(lower)) !== null) {
      used += m[0].length;
      var n = parseFloat(m[1]);
      switch (m[2]) {
        case 'ms': total += n; break;
        case 's': total += n * 1000; break;
        case 'm': total += n * 60000; break;
        case 'h': total += n * 3600000; break;
        default: throw err("bad time unit '" + m[2] + "' in T#" + s);
      }
    }
    if (used !== lower.length || used === 0) throw err('bad time literal T#' + s);
    return total;
  }

  /* ---------------------------------------------------------------- library */
  var libCache = null, libError = null;
  function library(src) {
    if (libCache) return libCache;
    var defs = {};
    try { new Parser(lex(src || ''), defs, null).defsOnly(); }
    catch (e) { if (e instanceof StError) { defs = {}; libError = 'the built-in block library failed to load: ' + e.message; } else throw e; }
    libCache = defs; return defs;
  }

  /* ---------------------------------------------------------------- parser */
  function Parser(toks, defs, size) {
    this.t = toks; this.p = 0; this.defs = defs; this.scope = ''; this.insts = {}; this.stack = [];
    this.depth = 0; this.nodes = 0; this.size = size || 2048;
    this.prog = { vars: [], byName: {}, body: [] };
  }
  var P = Parser.prototype;
  P.cur = function () { return this.p < this.t.length ? this.t[this.p] : { k: 'end', x: '<end>', line: this.t.length ? this.t[this.t.length - 1].line : 1 }; };
  P.isKw = function (k) { var c = this.cur(); return c.k === 'id' && c.x.toUpperCase() === k; };
  P.isSym = function (s) { var c = this.cur(); return c.k === 'sym' && c.x === s; };
  P.err = function (m) { return new StError(m, this.cur().line); };
  P.expectKw = function (k) { if (!this.isKw(k)) throw this.err('expected ' + k + " but found '" + this.cur().x + "'"); this.p++; };
  P.expectSym = function (s) { if (!this.isSym(s)) throw this.err("expected '" + s + "' but found '" + this.cur().x + "'"); this.p++; };
  P.expectId = function () { if (this.cur().k !== 'id') throw this.err("expected a name but found '" + this.cur().x + "'"); return this.t[this.p++].x; };
  P.enter = function () { if (++this.depth > MAX_DEPTH) throw this.err('program is nested too deeply (limit ' + MAX_DEPTH + ' levels)'); };
  P.find = function (name) { return this.prog.byName[name.toUpperCase()] || null; };
  P.addVar = function (v) { this.prog.vars.push(v); this.prog.byName[v.name.toUpperCase()] = v; };

  P.defsOnly = function () {
    while (this.isKw('FUNCTION_BLOCK')) this.fbDef();
    if (this.cur().k !== 'end') throw this.err("expected FUNCTION_BLOCK but found '" + this.cur().x + "'");
  };

  P.program = function () {
    while (this.isKw('FUNCTION_BLOCK')) this.fbDef();
    this.expectKw('PROGRAM'); this.expectId();
    while (this.isKw('VAR') || this.isKw('VAR_INPUT') || this.isKw('VAR_OUTPUT')) this.parseVars();
    this.block(this.prog.body, ['END_PROGRAM']);
    this.expectKw('END_PROGRAM');
    return this.prog;
  };

  P.fbDef = function () {
    this.expectKw('FUNCTION_BLOCK');
    var def = { name: this.expectId(), toks: this.t, inputs: {}, decl: 0, body: 0 };
    var up = def.name.toUpperCase();
    if (this.defs[up] || up === 'TON' || TYPES[up]) throw this.err('a block or type named ' + def.name + ' already exists');
    def.decl = this.p;
    while (this.isKw('VAR_INPUT') || this.isKw('VAR_OUTPUT') || this.isKw('VAR')) {
      var input = this.isKw('VAR_INPUT'); this.p++;
      while (!this.isKw('END_VAR')) {
        if (this.cur().k === 'end') throw this.err('missing END_VAR');
        var names = [this.expectId()];
        while (this.isSym(',')) { this.p++; names.push(this.expectId()); }
        this.expectSym(':');
        while (!this.isSym(';')) { if (this.cur().k === 'end') throw this.err("missing ';' in a declaration"); this.p++; }
        this.p++;
        if (input) for (var i = 0; i < names.length; i++) def.inputs[names[i].toUpperCase()] = true;
      }
      this.p++;
    }
    def.body = this.p;
    while (!this.isKw('END_FUNCTION_BLOCK')) { if (this.cur().k === 'end') throw this.err('missing END_FUNCTION_BLOCK for ' + def.name); this.p++; }
    this.p++;
    this.defs[up] = def;
    // trial expansion so mistakes are reported even when nothing uses the block yet
    var sp = this.prog, sn = this.nodes, si = this.insts;
    this.prog = { vars: [], byName: {}, body: [] }; this.insts = {};
    try { this.createInstance('_chk', def); this.inlineBody(def, '_chk.', []); }
    finally { this.prog = sp; this.nodes = sn; this.insts = si; }
  };

  P.createInstance = function (full, def) {
    if (this.stack.indexOf(def.name) >= 0) throw this.err('block ' + def.name + ' cannot contain itself');
    if (Object.keys(this.insts).length >= MAX_INSTANCES) throw this.err('too many function block instances (limit ' + MAX_INSTANCES + ')');
    this.insts[full.toUpperCase()] = def;
    var st = this.t, sp = this.p, ss = this.scope;
    this.stack.push(def.name);
    try {
      this.t = def.toks; this.p = def.decl; this.scope = full + '.';
      while (this.isKw('VAR_INPUT') || this.isKw('VAR_OUTPUT') || this.isKw('VAR')) this.parseVars();
    } finally { this.stack.pop(); this.t = st; this.p = sp; this.scope = ss; }
  };

  P.inlineBody = function (def, prefix, into) {
    if (this.stack.indexOf(def.name) >= 0) throw this.err('block ' + def.name + ' cannot call itself');
    var st = this.t, sp = this.p, ss = this.scope;
    this.stack.push(def.name);
    try { this.t = def.toks; this.p = def.body; this.scope = prefix; this.block(into, ['END_FUNCTION_BLOCK']); }
    finally { this.stack.pop(); this.t = st; this.p = sp; this.scope = ss; }
  };

  P.constValue = function () {
    var neg = false, v;
    if (this.isSym('-')) { neg = true; this.p++; }
    var c = this.cur();
    if (c.k === 'num') { v = parseFloat(c.x); this.p++; }
    else if (c.k === 'time') { v = parseTime(c.x, this.err.bind(this)); this.p++; }
    else if (this.isKw('TRUE')) { this.p++; v = 1; }
    else if (this.isKw('FALSE')) { this.p++; v = 0; }
    else throw this.err('expected a constant value');
    return neg ? -v : v;
  };

  P.parseVars = function () {
    var open = this.cur().x.toUpperCase();
    if (this.scope === '' && open !== 'VAR') throw this.err(open + ' can only be used inside a FUNCTION_BLOCK (use VAR in the PROGRAM)');
    this.p++;
    while (!this.isKw('END_VAR')) {
      if (this.cur().k === 'end') throw this.err('missing END_VAR');
      var names = [this.expectId()], addr = null;
      if (this.isKw('AT')) {
        this.p++;
        if (this.cur().k !== 'addr') throw this.err('expected an address like %IW0 after AT');
        addr = this.t[this.p++].x;
      } else while (this.isSym(',')) { this.p++; names.push(this.expectId()); }
      this.expectSym(':');
      var type = this.expectId().toUpperCase(), init = 0;
      if (this.isSym(':=')) { this.p++; init = this.constValue(); }
      this.expectSym(';');
      for (var i = 0; i < names.length; i++) {
        var full = this.scope + names[i];
        if (this.find(full) || this.insts[full.toUpperCase()]) throw this.err("variable '" + names[i] + "' is declared twice");
        if (type !== 'TON' && this.defs[type]) {
          if (addr) throw this.err('a function block cannot have an address (AT)');
          this.createInstance(full, this.defs[type]); continue;
        }
        var v = { name: full, type: type, v: init, init: init, timer: type === 'TON', el: 0, q: false, preset: 0, bound: false, out: false, table: null, addr: 0 };
        if (type !== 'TON' && !TYPES[type]) throw this.err('type ' + type + ' is not supported (use BOOL INT UINT DINT REAL TIME TON, or a function block name)');
        if (addr) {
          if (this.scope !== '') throw this.err('addresses (AT) are only allowed in the PROGRAM; pass the value into the block through an input');
          this.bind(v, addr);
        }
        this.addVar(v);
      }
    }
    this.expectKw('END_VAR');
  };

  P.bind = function (v, a) {
    if (a.length < 4 || a[0] !== '%') throw this.err('bad address ' + a);
    var dir = a[1], size = a[2], num = a.substring(3), addr, dot = num.indexOf('.');
    if (size === 'X' && dot >= 0) {
      var by = parseInt(num.substring(0, dot), 10), bit = parseInt(num.substring(dot + 1), 10);
      if (isNaN(by) || isNaN(bit) || bit < 0 || bit > 7) throw this.err('bad bit address ' + a);
      addr = by * 8 + bit;
    } else { addr = parseInt(num, 10); if (isNaN(addr) || String(addr) !== num) throw this.err('bad address ' + a); }
    if (addr < 0 || addr >= this.size) throw this.err('address out of range ' + a);
    if (dir === 'I' && size === 'X') { v.table = 'DI'; v.type = 'BOOL'; }
    else if (dir === 'Q' && size === 'X') { v.table = 'COIL'; v.out = true; v.type = 'BOOL'; }
    else if (dir === 'I' && size === 'W') v.table = 'IR';
    else if (dir === 'Q' && size === 'W') { v.table = 'HR'; v.out = true; }
    else throw this.err('address must be %IX %QX %IW or %QW: ' + a);
    v.bound = true; v.addr = addr;
  };

  P.block = function (into, enders) {
    for (;;) {
      for (var i = 0; i < enders.length; i++) if (this.isKw(enders[i])) return;
      if (this.cur().k === 'end') throw this.err('missing ' + enders[0]);
      into.push(this.stmt());
    }
  };

  P.stmt = function () {
    if (++this.nodes > MAX_NODES) throw this.err('program expands to too many statements (limit ' + MAX_NODES + '); each function block call copies the block in');
    this.enter();
    try { return this.stmtCore(); } finally { this.depth--; }
  };

  P.stmtCore = function () {
    var self = this;
    if (this.isKw('IF')) {
      this.p++;
      var conds = [this.expr()], blocks = [], b = [];
      this.expectKw('THEN'); this.block(b, ['ELSIF', 'ELSE', 'END_IF']); blocks.push(b);
      while (this.isKw('ELSIF')) { this.p++; conds.push(this.expr()); this.expectKw('THEN'); var b2 = []; this.block(b2, ['ELSIF', 'ELSE', 'END_IF']); blocks.push(b2); }
      var els = null;
      if (this.isKw('ELSE')) { this.p++; els = []; this.block(els, ['END_IF']); }
      this.expectKw('END_IF'); this.expectSym(';');
      return function (c) {
        for (var i = 0; i < conds.length; i++) if (conds[i](c) !== 0) { for (var j = 0; j < blocks[i].length; j++) blocks[i][j](c); return; }
        if (els) for (var k = 0; k < els.length; k++) els[k](c);
      };
    }
    if (this.isKw('WHILE')) {
      this.p++;
      var cond = this.expr(), body = [];
      this.expectKw('DO'); this.block(body, ['END_WHILE']); this.expectKw('END_WHILE'); this.expectSym(';');
      return function (c) {
        var n = 0;
        while (cond(c) !== 0) {
          if (++n > 1000 || ++c.loops > 20000) throw new Error('WHILE loop ran too long (scan overrun)');
          for (var i = 0; i < body.length; i++) body[i](c);
        }
      };
    }
    var name = this.expectId(), full = this.scope + name;
    if (this.insts[full.toUpperCase()]) return this.fbCall(full, this.insts[full.toUpperCase()]);
    var v = this.find(full);
    if (!v) throw this.err("unknown variable '" + name + "'");
    if (v.timer) {
      var IN = null, PT = null;
      this.expectSym('(');
      while (!this.isSym(')')) {
        var arg = this.expectId().toUpperCase();
        this.expectSym(':=');
        var e = this.expr();
        if (arg === 'IN') IN = e; else if (arg === 'PT') PT = e; else throw this.err('TON has inputs IN and PT, not ' + arg);
        if (this.isSym(',')) this.p++;
      }
      this.expectSym(')'); this.expectSym(';');
      return function (c) {
        var inp = IN ? IN(c) !== 0 : false, pt = PT ? PT(c) : v.preset;
        v.preset = pt;
        if (inp) { v.el = Math.min(v.el + c.dt, pt); v.q = v.el >= pt; } else { v.el = 0; v.q = false; }
      };
    }
    if (!v.out && v.bound) throw this.err("'" + name + "' is an input (" + (v.table === 'DI' ? '%IX' : '%IW') + ') and cannot be assigned');
    this.expectSym(':=');
    var val = this.expr(); this.expectSym(';');
    return assign(v, val);
  };

  function assign(v, val) {
    var t = v.type;
    if (t === 'BOOL') return function (c) { v.v = val(c) !== 0 ? 1 : 0; };
    if (t === 'INT' || t === 'DINT' || t === 'UINT' || t === 'WORD') return function (c) { v.v = Math.trunc(val(c)); };
    return function (c) { v.v = val(c); };
  }

  P.fbCall = function (full, def) {
    var seq = [];
    this.expectSym('(');
    while (!this.isSym(')')) {
      var arg = this.expectId();
      if (!def.inputs[arg.toUpperCase()]) throw this.err(def.name + " has no input '" + arg + "'");
      this.expectSym(':=');
      var e = this.expr(), target = this.find(full + '.' + arg);
      if (!target) throw this.err(def.name + " input '" + arg + "' cannot be set here");
      seq.push(assign(target, e));
      if (this.isSym(',')) this.p++;
    }
    this.expectSym(')'); this.expectSym(';');
    this.inlineBody(def, full + '.', seq);
    return function (c) { for (var i = 0; i < seq.length; i++) seq[i](c); };
  };

  P.expr = function () { this.enter(); try { return this.or(); } finally { this.depth--; } };
  function bin(op, a, b) {
    switch (op) {
      case 'OR': return function (c) { var x = a(c), y = b(c); return (x !== 0 || y !== 0) ? 1 : 0; };
      case 'XOR': return function (c) { return ((a(c) !== 0) !== (b(c) !== 0)) ? 1 : 0; };
      case 'AND': return function (c) { var x = a(c), y = b(c); return (x !== 0 && y !== 0) ? 1 : 0; };
      case '=': return function (c) { return a(c) === b(c) ? 1 : 0; };
      case '<>': return function (c) { return a(c) !== b(c) ? 1 : 0; };
      case '<': return function (c) { return a(c) < b(c) ? 1 : 0; };
      case '>': return function (c) { return a(c) > b(c) ? 1 : 0; };
      case '<=': return function (c) { return a(c) <= b(c) ? 1 : 0; };
      case '>=': return function (c) { return a(c) >= b(c) ? 1 : 0; };
      case '+': return function (c) { return a(c) + b(c); };
      case '-': return function (c) { return a(c) - b(c); };
      case '*': return function (c) { return a(c) * b(c); };
      case '/': return function (c) { var d = b(c), n = a(c); return d === 0 ? 0 : n / d; };
      case 'MOD': return function (c) { var d = b(c), n = a(c); return d === 0 ? 0 : n % d; };
    }
    throw new Error('bad operator ' + op);
  }
  P.or = function () { var a = this.xor(); while (this.isKw('OR')) { this.p++; a = bin('OR', a, this.xor()); } return a; };
  P.xor = function () { var a = this.and(); while (this.isKw('XOR')) { this.p++; a = bin('XOR', a, this.and()); } return a; };
  P.and = function () { var a = this.eq(); while (this.isKw('AND')) { this.p++; a = bin('AND', a, this.eq()); } return a; };
  P.eq = function () { var a = this.rel(); while (this.isSym('=') || this.isSym('<>')) { var o = this.t[this.p++].x; a = bin(o, a, this.rel()); } return a; };
  P.rel = function () { var a = this.add(); while (this.isSym('<') || this.isSym('>') || this.isSym('<=') || this.isSym('>=')) { var o = this.t[this.p++].x; a = bin(o, a, this.add()); } return a; };
  P.add = function () { var a = this.mul(); while (this.isSym('+') || this.isSym('-')) { var o = this.t[this.p++].x; a = bin(o, a, this.mul()); } return a; };
  P.mul = function () { var a = this.unary(); while (this.isSym('*') || this.isSym('/') || this.isKw('MOD')) { var o = this.t[this.p++].x.toUpperCase(); a = bin(o, a, this.unary()); } return a; };
  P.unary = function () { this.enter(); try { return this.unaryCore(); } finally { this.depth--; } };
  P.unaryCore = function () {
    if (this.isKw('NOT')) { this.p++; var a = this.unary(); return function (c) { return a(c) === 0 ? 1 : 0; }; }
    if (this.isSym('-')) { this.p++; var b = this.unary(); return function (c) { return -b(c); }; }
    return this.primary();
  };
  P.primary = function () {
    var cur = this.cur();
    if (cur.k === 'num') { this.p++; var nv = parseFloat(cur.x); return function () { return nv; }; }
    if (cur.k === 'time') { this.p++; var tv = parseTime(cur.x, this.err.bind(this)); return function () { return tv; }; }
    if (this.isSym('(')) { this.p++; var e = this.expr(); this.expectSym(')'); return e; }
    if (this.isKw('TRUE')) { this.p++; return function () { return 1; }; }
    if (this.isKw('FALSE')) { this.p++; return function () { return 0; }; }
    if (cur.k === 'id') {
      var name = cur.x, up = name.toUpperCase(); this.p++;
      if (this.isSym('(')) {
        if (up !== 'ABS' && up !== 'MIN' && up !== 'MAX' && up !== 'LIMIT' && up !== 'DT') throw this.err('unknown function ' + name);
        this.p++;
        var args = [];
        while (!this.isSym(')')) { args.push(this.expr()); if (this.isSym(',')) this.p++; else break; }
        this.expectSym(')');
        var need = up === 'ABS' ? 1 : up === 'LIMIT' ? 3 : up === 'DT' ? 0 : 2;
        if (args.length !== need) throw this.err(up + ' needs ' + need + ' arguments');
        switch (up) {
          case 'ABS': return function (c) { return Math.abs(args[0](c)); };
          case 'MIN': return function (c) { return Math.min(args[0](c), args[1](c)); };
          case 'MAX': return function (c) { return Math.max(args[0](c), args[1](c)); };
          case 'LIMIT': return function (c) { return Math.max(args[0](c), Math.min(args[1](c), args[2](c))); };
          default: return function (c) { return c.dt; };
        }
      }
      var full = this.scope + name, inst = this.insts[full.toUpperCase()];
      if (inst) {
        if (!this.isSym('.')) throw this.err('use ' + name + '.<name>, for example ' + name + '.Q');
        this.p++;
        var mem = this.expectId(), mv = this.find(full + '.' + mem);
        if (!mv) throw this.err('block ' + inst.name + " has no variable '" + mem + "'");
        if (mv.timer) throw this.err("'" + mem + "' is a timer inside the block and cannot be read from outside");
        return function () { return mv.v; };
      }
      var v = this.find(full);
      if (!v) throw this.err("unknown variable '" + name + "'");
      if (this.isSym('.')) {
        this.p++;
        var member = this.expectId().toUpperCase();
        if (!v.timer || (member !== 'Q' && member !== 'ET')) throw this.err('only timers have .Q and .ET');
        return member === 'Q' ? function () { return v.q ? 1 : 0; } : function () { return v.el; };
      }
      if (v.timer) throw this.err('use ' + name + '.Q or ' + name + '.ET');
      return function () { return v.v; };
    }
    throw this.err("unexpected '" + cur.x + "'");
  };

  /* ---------------------------------------------------------------- compile / runtime */
  function compile(src, libSrc, size) {
    try {
      var toks = lex(src);
      if (toks.length > MAX_TOKENS) throw new StError('program is too long (limit ' + MAX_TOKENS + ' tokens)', 1);
      var defs = {}, lib = library(libSrc);
      for (var k in lib) defs[k] = lib[k];
      var prog = new Parser(toks, defs, size).program();
      return { prog: prog, error: null };
    } catch (e) {
      if (e instanceof StError) {
        var msg = e.message;
        if (libError && msg.indexOf('not supported') >= 0) msg += ' (' + libError + ')';
        return { prog: null, error: msg };
      }
      throw e;
    }
  }

  var SAMPLE = "(* Structured Text sample. Edit, press Compile, then Run.\n   Addresses are the Address column of the I/O list:\n   %IX n = discrete input n    %QX n = coil n    (also %IX0.3 = bit 3)\n   %IW n = input register n    %QW n = holding register n\n   Find real addresses in the table on the right, then replace the examples. *)\nPROGRAM Main\nVAR\n    // Pressure AT %IW0 : INT;\n    // Pump    AT %QX0 : BOOL;\n    Counter : INT := 0;\n    Delay   : TON;\nEND_VAR\n\nDelay(IN := TRUE, PT := T#2s);\nIF Delay.Q THEN\n    Counter := Counter + 1;\n    Delay(IN := FALSE, PT := T#2s);\nEND_IF;\nEND_PROGRAM\n";

  /* The PLC. `store` = { DI:[], COIL:[], IR:[], HR:[], coilAssigned:[], hrAssigned:[] } shared with the simulated devices. */
  function Plc(store, libSrc) {
    this.store = store; this.libSrc = libSrc; this.size = store.DI.length;
    this.source = SAMPLE; this.prog = null; this.compileError = ''; this.fault = '';
    this.running = false; this.scans = 0; this.scanMs = 0; this.forces = {}; this.blockExternal = true;
    this.last = 0; this.next = 0; this.pendingMode = 0;
    this.install(this.source);
  }
  Plc.prototype.install = function (src) {
    var r = compile(src, this.libSrc, this.size);
    this.source = src;
    if (!r.prog) { this.compileError = r.error; return; }
    this.compileError = ''; this.prog = r.prog; this.running = false; this.fault = '';
  };
  Plc.prototype.validate = function (prog) {
    for (var i = 0; i < prog.vars.length; i++) {
      var v = prog.vars[i];
      if (!v.bound) continue;
      if (v.table === 'COIL' && !this.store.coilAssigned[v.addr]) return '%QX' + v.addr + ' (variable ' + v.name + ') is not an assigned coil. Check the I/O list.';
      if (v.table === 'HR' && !this.store.hrAssigned[v.addr]) return '%QW' + v.addr + ' (variable ' + v.name + ') is not an assigned holding register. Check the I/O list.';
    }
    return null;
  };
  Plc.prototype.check = function (src) {
    var r = compile(src || '', this.libSrc, this.size);
    var warn = r.prog ? (this.validate(r.prog) || '') : '';
    return { ok: !!r.prog, error: r.error || '', warning: warn };
  };
  Plc.prototype.readPoint = function (v) {
    var s = this.store;
    return v.table === 'DI' ? (s.DI[v.addr] ? 1 : 0) : v.table === 'COIL' ? (s.COIL[v.addr] ? 1 : 0) : v.table === 'IR' ? s.IR[v.addr] : s.HR[v.addr];
  };
  Plc.prototype.requestMode = function (run, now) {
    if (!run) { this.running = false; return; }
    if (!this.prog) { this.fault = 'Cannot run: the program does not compile. ' + this.compileError; return; }
    var why = this.validate(this.prog);
    if (why) { this.fault = 'Cannot run: ' + why; return; }
    this.fault = '';
    for (var i = 0; i < this.prog.vars.length; i++) {
      var v = this.prog.vars[i];
      v.el = 0; v.q = false;
      if (!v.bound && v.name.indexOf('.') >= 0) v.v = v.init;
      if (v.bound && v.out) v.v = this.readPoint(v);
    }
    this.last = now; this.next = now; this.running = true;
  };
  Plc.prototype.force = function (name, value) {
    if (value === null) { delete this.forces[name.toUpperCase()]; return; }
    var v = this.prog ? this.prog.byName[name.toUpperCase()] : null;
    if (!v || v.timer || (v.bound && v.out)) return;
    this.forces[name.toUpperCase()] = value;
  };
  /* Call often. now = ms. Runs one scan when due. */
  Plc.prototype.tick = function (now, scanMs) {
    if (!this.running || !this.prog || now < this.next) return;
    this.next = now + Math.max(20, scanMs || 100);
    var dt = Math.max(0, now - this.last); this.last = now;
    var t0 = (typeof performance !== 'undefined' ? performance.now() : Date.now());
    try { this.scan(dt); }
    catch (e) { this.running = false; this.fault = e.message; }
    this.scanMs = (typeof performance !== 'undefined' ? performance.now() : Date.now()) - t0;
    this.scans++;
  };
  Plc.prototype.scan = function (dt) {
    var prog = this.prog, s = this.store, i, v;
    for (i = 0; i < prog.vars.length; i++) { v = prog.vars[i]; if (v.bound && !v.out) v.v = this.readPoint(v); }
    for (i = 0; i < prog.vars.length; i++) {
      v = prog.vars[i];
      var fv = this.forces[v.name.toUpperCase()];
      if (fv !== undefined && !v.timer && !(v.bound && v.out)) v.v = v.type === 'BOOL' ? (fv !== 0 ? 1 : 0) : fv;
    }
    var ctx = { dt: dt, loops: 0 };
    for (i = 0; i < prog.body.length; i++) prog.body[i](ctx);
    for (i = 0; i < prog.vars.length; i++) {
      v = prog.vars[i];
      if (!v.bound || !v.out) continue;
      if (v.table === 'COIL') s.COIL[v.addr] = v.v !== 0;
      else { var r = Math.round(v.v); s.HR[v.addr] = r > 32767 ? 32767 : r < -32768 ? -32768 : r; }
    }
  };
  function fmt(n) { var r = parseFloat(n.toFixed(3)); return String(r); }
  Plc.prototype.stateJson = function (prefix) {
    var vars = [];
    if (this.prog) for (var i = 0; i < this.prog.vars.length; i++) {
      var v = this.prog.vars[i];
      var val = v.timer ? (v.q ? 'Q=1 ET=' : 'Q=0 ET=') + Math.floor(v.el) + 'ms' : fmt(v.v);
      var at = v.bound ? (v.out ? '%Q' : '%I') + (v.table === 'COIL' || v.table === 'DI' ? 'X' : 'W') + v.addr : '';
      var o = { n: v.name, t: v.type, at: at, v: val };
      if (this.forces[v.name.toUpperCase()] !== undefined) o.f = 1;
      vars.push(o);
    }
    return { running: this.running, scans: this.scans, scanMs: this.scanMs.toFixed(3), blockExternal: this.blockExternal,
             prefix: prefix || '', compileError: this.compileError, fault: this.fault, vars: vars };
  };

  root.StSim = { compile: compile, Plc: Plc, SAMPLE: SAMPLE, lex: lex };
  if (typeof module !== 'undefined' && module.exports) module.exports = root.StSim;
})(typeof window !== 'undefined' ? window : globalThis);
