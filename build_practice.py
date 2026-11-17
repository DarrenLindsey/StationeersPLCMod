#!/usr/bin/env python3
"""Builds StationeersPLC-Practice.html: the workstation page plus the simulator, in one file that opens offline."""
import re, os, sys, json
here = os.path.dirname(os.path.abspath(__file__))
rd = lambda *p: open(os.path.join(here, *p), encoding='utf-8').read()
html = rd('WebUi', 'index.html')
cs = rd('StLibrary.cs')
lib = cs.split('Source = @"', 1)[1].split('";\n    }', 1)[0]
js = lambda s: s.replace('</script', '<\\/script')
head = '<script>window.PLC_LIB=' + json.dumps(lib) + ';</script>\n' + ''.join('<script>' + js(rd('Sim', f)) + '</script>\n' for f in ('engine.js', 'world.js', 'shim.js'))
tail = '<script>' + js(rd('Sim', 'bench.js')) + '</script>\n'
html = html.replace('<body>', '<body>\n' + head, 1)
html = html.replace('</script></body></html>', '</script>\n' + tail + '</body></html>', 1)
html = html.replace('<h1>Stationeers PLC Workstation</h1>', '<h1>Stationeers PLC Workstation <small style="font-weight:400;color:var(--warn)">Practice Bench (offline)</small></h1>', 1)
out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, 'StationeersPLC-Practice.html')
open(out, 'w', encoding='utf-8').write(html)
print(out, len(html))
