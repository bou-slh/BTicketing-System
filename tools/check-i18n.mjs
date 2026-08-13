#!/usr/bin/env node
// i18n + link checker for the RapidsolDestek repo.
// Checks BOTH sides of the spec boundary:
//   1. mockups/: every data-i18n* key resolves in TR and EN (shared dicts + PAGE_I18N), links resolve.
//   2. src/RapidsolDestek.Web/Resources/: every .resx has a matching .en.resx with an identical key set.
// Exit code != 0 on any problem. Run: node tools/check-i18n.mjs [--mockups-only|--resx-only]
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
let problems = 0;
const report = (...m) => { console.error('✗', ...m); problems++; };

// ---- 1. Mockups ------------------------------------------------------------
if (!args.includes('--resx-only')) {
  const M = path.join(ROOT, 'mockups');
  const loadShared = (f) => {
    const ctx = { window: {} };
    vm.createContext(ctx);
    vm.runInContext(fs.readFileSync(path.join(M, 'assets/js', f), 'utf8'), ctx);
    let out = {};
    for (const v of Object.values(ctx.window)) if (typeof v === 'object') out = { ...out, ...v };
    return out;
  };
  const shared = { tr: loadShared('i18n-tr.js'), en: loadShared('i18n-en.js') };
  let pages = 0;
  for (const dir of ['agent', 'admin', 'portal']) {
    for (const f of fs.readdirSync(path.join(M, dir)).filter((x) => x.endsWith('.html'))) {
      pages++;
      const html = fs.readFileSync(path.join(M, dir, f), 'utf8');
      const m = html.match(/window\.PAGE_I18N\s*=\s*(\{[\s\S]*?\});\s*<\/script>/);
      let page = { tr: {}, en: {} };
      if (m) {
        try { page = vm.runInNewContext('(' + m[1] + ')'); }
        catch (e) { report('PAGE_I18N parse failure', `${dir}/${f}`, e.message); continue; }
      }
      for (const k of [...html.matchAll(/data-i18n(?:-placeholder|-title|-label)?="([^"]+)"/g)].map((x) => x[1]))
        for (const lang of ['tr', 'en'])
          if (!(k in (page[lang] || {})) && !(k in shared[lang])) report('missing', lang, k, 'in', `${dir}/${f}`);
      for (const k of Object.keys(page.tr || {})) if (!(k in (page.en || {}))) report('TR-only key', k, 'in', `${dir}/${f}`);
      for (const k of Object.keys(page.en || {})) if (!(k in (page.tr || {}))) report('EN-only key', k, 'in', `${dir}/${f}`);
    }
  }
  // links
  for (const dir of ['agent', 'admin', 'portal', '.']) {
    for (const f of fs.readdirSync(path.join(M, dir)).filter((x) => x.endsWith('.html'))) {
      const html = fs.readFileSync(path.join(M, dir, f), 'utf8');
      for (const m of html.matchAll(/href="([^"#][^"]*)"/g)) {
        const h = m[1];
        if (h.startsWith('http') || h.startsWith('mailto')) continue;
        if (!fs.existsSync(path.join(M, dir, h.split('#')[0]))) report('broken link', `${dir}/${f}`, '→', h);
      }
    }
  }
  console.log(`mockups: ${pages} pages checked`);
}

// ---- 2. resx ---------------------------------------------------------------
if (!args.includes('--mockups-only')) {
  const RES = path.join(ROOT, 'src/RapidsolDestek.Web/Resources');
  const keysOf = (file) =>
    new Set([...fs.readFileSync(file, 'utf8').matchAll(/<data name="([^"]+)"/g)].map((m) => m[1]));
  const walk = (d) =>
    fs.existsSync(d)
      ? fs.readdirSync(d, { withFileTypes: true }).flatMap((e) =>
          e.isDirectory() ? walk(path.join(d, e.name)) : [path.join(d, e.name)])
      : [];
  const all = walk(RES).filter((f) => f.endsWith('.resx'));
  let pairs = 0;
  for (const tr of all.filter((f) => !f.endsWith('.en.resx'))) {
    pairs++;
    const en = tr.replace(/\.resx$/, '.en.resx');
    if (!fs.existsSync(en)) { report('missing EN twin for', path.relative(ROOT, tr)); continue; }
    const a = keysOf(tr), b = keysOf(en);
    for (const k of a) if (!b.has(k)) report('key only in TR resx', k, path.relative(ROOT, tr));
    for (const k of b) if (!a.has(k)) report('key only in EN resx', k, path.relative(ROOT, en));
  }
  console.log(`resx: ${pairs} resource pairs checked`);
}

if (problems) { console.error(`\n${problems} problem(s)`); process.exit(1); }
console.log('ALL CLEAN');
