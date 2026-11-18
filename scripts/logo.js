#!/usr/bin/env node
// Writes the logo of MatRegistry in its levels of detail (docs/logo and src/MatRegistry/wwwroot/img/logo, the favicon of wwwroot):
//   mark      the full mark (64 x 64): three containers stacked like in a yard, each with its ribs, the top one lighter
//   favicon   the small mark (32 x 32): the same three containers, thicker, one rib each (three ribs would be mush at 16 px)
//   maskable  the mark without corners, the content inside the safe zone (Android adaptive icons)
//   mono      one colour (currentColor) for places that tint it; the ribs are cut out
//   logo      the mark with the word mark (light surfaces), logo-dark (dark surfaces)
// The glyph of the menu (24 x 24, a line icon like the toolkit's) is the symbol mat-i-matregistry of wwwroot/img/app-icons.svg.
// The PNG icons are made from these files with scripts/icons.ps1 (Edge headless).
//   node scripts/logo.js
const fs = require('fs');
const path = require('path');

const accent = '#4D7C0F';
const tint = '#DCEBC0';
const shade = '#365A08';
const light = '#A3CF5E';
const root = path.join(__dirname, '..');

// one container of the mark: the box and three ribs
function container(x, y, fill, rib) {
  const ribs = [5.25, 10.5, 15.75].map(dx => `M${x + dx} ${y + 4.5}v9`).join('');
  return `<rect x="${x}" y="${y}" width="21" height="18" rx="2.5" fill="${fill}"/><path d="${ribs}" stroke="${rib}" stroke-width="2" stroke-linecap="round"/>`;
}

// the inside of the 64 x 64 mark (without its background), so the logo and the maskable icon draw the same shapes
const markShapes = [container(21.5, 13, tint, shade), container(10, 33, '#fff', accent), container(33, 33, '#fff', accent)].join('');

const files = {
  'matregistry-mark.svg':
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatRegistry">\n` +
    `  <rect width="64" height="64" rx="14" fill="${accent}"/>\n  ${markShapes}\n</svg>\n`,
  'matregistry-favicon.svg':
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" role="img" aria-label="MatRegistry">\n` +
    `  <rect width="32" height="32" rx="7" fill="${accent}"/>\n` +
    `  <rect x="10" y="5" width="12" height="10" rx="1.6" fill="${tint}"/>\n` +
    `  <rect x="3.5" y="17" width="12" height="10" rx="1.6" fill="#fff"/>\n` +
    `  <rect x="16.5" y="17" width="12" height="10" rx="1.6" fill="#fff"/>\n` +
    `  <path d="M16 8v4" stroke="${shade}" stroke-width="2" stroke-linecap="round"/>\n` +
    `  <path d="M9.5 20v4M22.5 20v4" stroke="${accent}" stroke-width="2" stroke-linecap="round"/>\n</svg>\n`,
  'matregistry-maskable.svg':
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatRegistry">\n` +
    `  <rect width="64" height="64" fill="${accent}"/>\n  <g transform="translate(9.6 9.6) scale(0.7)">${markShapes}</g>\n</svg>\n`,
  'matregistry-mono.svg':
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatRegistry">\n` +
    `  <mask id="ribs"><rect width="64" height="64" fill="#fff"/>` +
    `<path d="M26.75 17.5v9M32 17.5v9M37.25 17.5v9M15.25 37.5v9M20.5 37.5v9M25.75 37.5v9M38.25 37.5v9M43.5 37.5v9M48.75 37.5v9" stroke="#000" stroke-width="2" stroke-linecap="round"/></mask>\n` +
    `  <g fill="currentColor" mask="url(#ribs)"><rect x="21.5" y="13" width="21" height="18" rx="2.5" opacity=".55"/>` +
    `<rect x="10" y="33" width="21" height="18" rx="2.5"/><rect x="33" y="33" width="21" height="18" rx="2.5"/></g>\n</svg>\n`,
  'matregistry-logo.svg': wordmark('#1F1E1B', accent),
  'matregistry-logo-dark.svg': wordmark('#F4F2EF', light),
};

function wordmark(text, second) {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 196 40" width="196" height="40" role="img" aria-label="MatRegistry">\n` +
    `  <rect x="2" y="2" width="36" height="36" rx="8" fill="${accent}"/><g transform="translate(2 2) scale(0.5625)">${markShapes}</g>\n` +
    `  <text x="48" y="28.5" font-family="'Segoe UI','Helvetica Neue',Arial,sans-serif" font-size="22" font-weight="700" letter-spacing="-0.4" fill="${text}">Mat<tspan fill="${second}">Registry</tspan></text>\n</svg>\n`;
}

for (const dir of [path.join(root, 'docs', 'logo'), path.join(root, 'src', 'MatRegistry', 'wwwroot', 'img', 'logo')]) {
  fs.mkdirSync(dir, { recursive: true });
  for (const [name, svg] of Object.entries(files)) {
    fs.writeFileSync(path.join(dir, name), svg);
  }
}

fs.writeFileSync(path.join(root, 'src', 'MatRegistry', 'wwwroot', 'favicon.svg'), files['matregistry-favicon.svg']);
console.log(`wrote ${Object.keys(files).length} files into docs/logo and wwwroot/img/logo, and wwwroot/favicon.svg`);
