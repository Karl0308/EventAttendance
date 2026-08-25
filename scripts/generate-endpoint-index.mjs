// Generates `docs/api/endpoints.md` — the one-page endpoint index — from `docs/api/openapi.json`.
//
// Phase 4e deleted 443 lines of hand-written contract because a stale table looked exactly like a
// current one: nothing failed when an edit was missed. A hand-written endpoint index would have that
// same failure mode, so this file exists instead. The index is derived, never authored, and
// `EndpointIndexTests.The_committed_endpoint_index_is_the_generated_one` fails the build when the
// committed copy stops matching — the same guard `OpenApiDocumentTests` puts on `openapi.json`.
//
//   node scripts/generate-endpoint-index.mjs            assert (exits 1 on drift)
//   node scripts/generate-endpoint-index.mjs --write     rewrite the committed index
//
// Shapes are deliberately NOT reproduced here. Field-level detail belongs in openapi.json, which
// codegen reads; duplicating it into markdown is how a second contract gets born.

import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..');
const SOURCE = 'docs/api/openapi.json';
const TARGET = 'docs/api/endpoints.md';

const METHODS = ['get', 'post', 'put', 'patch', 'delete', 'head', 'options', 'trace'];

/** The route as a caller types it — the `/api/v1` mount is stated once, in the preamble. */
const relative = (path) => path.replace(/^\/api\/v1/, '') || '/';

/**
 * First sentence of the summary, minus the leading "`GET /foo` — " the XML comments open with.
 * The route is already the row's first column; repeating it there costs the width the description
 * needs. Falls back to the description when a summary is absent.
 *
 * The summaries are XML doc comments, so they carry `<b>`/`<c>`/`<em>` markup that means nothing to
 * a markdown table — those are converted rather than passed through, and stripping the route prefix
 * frequently leaves a lowercase opener that has to be recapitalised.
 */
function blurb(op) {
  const text = (op.summary || op.description || '').replace(/\r?\n/g, ' ').trim();
  if (!text) return '';

  const markdown = text
    .replace(/<\/?(?:b|strong)>/g, '**')
    .replace(/<\/?(?:em|i)>/g, '*')
    .replace(/<c>([^<]*)<\/c>/g, '`$1`')
    .replace(/<[^>]+>/g, '');

  // Anchored on the closing backtick, not on the first dash: routes contain hyphens
  // (`/academic/course-offerings`), so a dash-terminated pattern cuts mid-route.
  const withoutRoute = markdown.replace(/^`[A-Z]+ [^`]+`\s*[—–-]\s*/, '');
  const sentence = withoutRoute.match(/^.*?[.!?](?=\s|$)/)?.[0] ?? withoutRoute;

  const cleaned = sentence.replace(/\s+/g, ' ').replace(/\|/g, '\\|').trim();

  // Recapitalise only a bare leading letter — never inside `code` or **bold**, where the character
  // is the author's and an uppercase swap would misquote a field name.
  return cleaned.replace(/^[a-z]/, (c) => c.toUpperCase());
}

/**
 * The scheme(s) the operation demands, or open (ADR-001 D-6).
 *
 * Read off `op.security`, which the generator derives from each endpoint's own `[Authorize]` — so a
 * scheme added to the API appears here without this file being edited, and one that is *not* in
 * `op.security` renders as open, which is the honest answer. Two schemes exist since Phase 6b:
 * `DeviceKey` for a capture device, `Bearer` for a signed-in person.
 */
const auth = (op) => {
  const schemes = [...new Set((op.security ?? []).flatMap((r) => Object.keys(r)))].sort();
  return schemes.length ? schemes.map((s) => `**${s}**`).join(' ') : '—';
};

/** Non-2xx codes, so the row shows what a caller has to branch on without opening the schema. */
function failures(op) {
  const codes = Object.keys(op.responses ?? {})
    .filter((c) => /^\d+$/.test(c) && Number(c) >= 400)
    .sort();

  // An endpoint declaring no failure is not an endpoint that cannot fail: `[ApiController]` model
  // validation returns an undeclared 400 on any of them. Said as "none declared" rather than a bare
  // dash so the column is not read as a promise the API does not make.
  return codes.length ? codes.map((c) => `\`${c}\``).join(' ') : '*none declared*';
}

function render(doc) {
  const groups = new Map();
  let count = 0;

  for (const [path, item] of Object.entries(doc.paths)) {
    for (const method of METHODS) {
      const op = item[method];
      if (!op) continue;
      const tag = op.tags?.[0] ?? 'Untagged';
      if (!groups.has(tag)) groups.set(tag, []);
      groups.get(tag).push({ method: method.toUpperCase(), path, op });
      count += 1;
    }
  }

  const tagOrder = (doc.tags ?? []).map((t) => t.name);
  const sortedTags = [...groups.keys()].sort((a, b) => {
    const ai = tagOrder.indexOf(a);
    const bi = tagOrder.indexOf(b);
    return (ai === -1 ? Infinity : ai) - (bi === -1 ? Infinity : bi) || a.localeCompare(b);
  });

  const out = [];
  out.push(`# ${doc.info?.title ?? 'API'} — endpoint index`);
  out.push('');
  out.push('<!-- GENERATED FILE — DO NOT EDIT.');
  out.push(`     Source: ${SOURCE}. Regenerate: node scripts/generate-endpoint-index.mjs --write -->`);
  out.push('');
  out.push(
    `**Generated from [\`openapi.json\`](openapi.json) — do not edit by hand.** ` +
      `${count} operations across ${sortedTags.length} controllers, all mounted under \`/api/v1\`. ` +
      `Routes below are written relative to that mount.`,
  );
  out.push('');
  out.push(
    'This page is a **map, not a contract.** It exists so you can find an endpoint; ' +
      'payload shapes, field types and outcome tokens live in [`openapi.json`](openapi.json), ' +
      'which is what you generate a client from. Behaviour a schema cannot state — what your ' +
      'queue does with each outcome, the card-UID and clock rules — is in ' +
      '[`attendance-contract-handoff.md`](attendance-contract-handoff.md).',
  );
  out.push('');
  out.push(
    '`Auth` names the credential an endpoint demands: **DeviceKey** for a capture device, ' +
      '**Bearer** for a signed-in person (`POST /auth/login`). Everything else is still open — ' +
      'enforcement over the rest of the surface is a later phase (ADR-001 D-6), so an unmarked row ' +
      'is not a public endpoint, it is an unprotected one.',
  );
  out.push('');

  out.push('## Contents');
  out.push('');
  for (const tag of sortedTags) {
    const anchor = tag.toLowerCase().replace(/[^a-z0-9]+/g, '-');
    out.push(`- [${tag}](#${anchor}) — ${groups.get(tag).length}`);
  }
  out.push('');

  for (const tag of sortedTags) {
    out.push(`## ${tag}`);
    out.push('');

    const rows = groups.get(tag).sort((a, b) => a.path.localeCompare(b.path) || a.method.localeCompare(b.method));

    out.push('| Method | Route | Auth | Errors | What it does |');
    out.push('|---|---|---|---|---|');
    for (const { method, path, op } of rows) {
      out.push(`| \`${method}\` | \`${relative(path)}\` | ${auth(op)} | ${failures(op)} | ${blurb(op)} |`);
    }
    out.push('');
  }

  return out.join('\n') + '\n';
}

const doc = JSON.parse(readFileSync(join(repo, SOURCE), 'utf8'));
const generated = render(doc);
const target = join(repo, TARGET);

if (process.argv.includes('--write')) {
  writeFileSync(target, generated);
  console.log(`Wrote ${TARGET} — ${generated.split('\n').length} lines.`);
} else {
  let committed = null;
  try {
    committed = readFileSync(target, 'utf8');
  } catch {
    console.error(`${TARGET} is missing. Regenerate: node scripts/generate-endpoint-index.mjs --write`);
    process.exit(1);
  }
  if (committed.replace(/\r\n/g, '\n') !== generated.replace(/\r\n/g, '\n')) {
    console.error(
      `${TARGET} is not what ${SOURCE} generates — the index has drifted from the contract. ` +
        `Regenerate it in the same change: node scripts/generate-endpoint-index.mjs --write`,
    );
    process.exit(1);
  }
  console.log(`${TARGET} matches ${SOURCE}.`);
}
