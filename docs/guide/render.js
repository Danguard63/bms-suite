// Makes docs\BMS-Suite-User-Guide.pdf from guide.html (needs Node and Playwright with Chromium)
const path = require('path');
const { chromium } = require('playwright');
(async () => {
  const b = await chromium.launch();
  const p = await b.newPage();
  await p.goto(require('url').pathToFileURL(path.join(__dirname, 'guide.html')).href, { waitUntil: 'networkidle' });
  await p.pdf({ path: path.join(__dirname, '..', 'BMS-Suite-User-Guide.pdf'), format: 'A4', printBackground: true, preferCSSPageSize: true,
    displayHeaderFooter: true, headerTemplate: '<div></div>',
    footerTemplate: '<div style="width:100%;font-size:7.5pt;color:#8a9097;padding:0 17mm;display:flex;justify-content:space-between;font-family:Segoe UI,Arial"><span>BMS Suite – User Guide</span><span class="pageNumber"></span></div>' });
  await b.close();
})();
