/* Run against the disposable server prepared by integration.py. */
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const state = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const output = path.dirname(path.resolve(process.argv[2]));
const rowSelector = '.actionSheet [data-id="download-all-subtitles"]';
const results = [];

async function login(page, user = 'review') {
    await page.goto(state.url + '/web/');
    await page.locator('#txtManualName').fill(user);
    await page.locator('#txtManualPassword').fill(state.password);
    await page.getByRole('button', { name: 'Sign In', exact: true }).click();
    await page.waitForURL('**/#/home*');
}

async function details(page, name) {
    if (await page.locator('.actionSheet').count()) {
        await page.mouse.click(10, 80);
        await page.locator('.actionSheet').waitFor({ state: 'detached' });
    }
    await page.goto('about:blank');
    await page.goto(state.url + '/web/#/details?id=' + state.items[name]);
    await page.locator('.btnMoreCommands:visible').click();
    await page.locator(rowSelector).waitFor();
    assert.equal(await page.locator(rowSelector).evaluate(e => e.previousElementSibling.dataset.id), 'download');
    assert.equal(await page.locator(rowSelector).innerText(), 'Download subtitles');
    assert.equal(await page.locator(rowSelector).getAttribute('data-item-id'), state.items[name]);
}

function passed(check) {
    results.push({ check, passed: true });
    console.log('PASS', check);
}

(async () => {
    const browser = await chromium.launch({ headless: true });
    try {
        const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true });
        const page = await context.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await login(page);
        await details(page, 'Subtitle Test');
        await page.screenshot({ path: path.join(output, 'download-subtitles-menu.png') });
        passed('movie popup places Download subtitles immediately below Download');

        const event = page.waitForEvent('download');
        await page.locator(rowSelector).click();
        const download = await event;
        assert.equal(download.suggestedFilename(), 'Subtitle Test.subtitles.zip');
        await download.saveAs(path.join(output, 'browser-subtitles.zip'));
        assert.equal(fs.readFileSync(path.join(output, 'browser-subtitles.zip')).subarray(0, 2).toString(), 'PK');
        await page.getByText('8 subtitle tracks downloaded.', { exact: false }).waitFor();
        passed('browser saves one ZIP containing eight tracks');

        await page.locator('.actionSheet').waitFor({ state: 'detached' });
        await page.locator('.btnMoreCommands:visible').click();
        await page.locator(rowSelector).waitFor();
        assert.equal(await page.locator(rowSelector).count(), 1);
        passed('reopening a popup does not duplicate the button');

        const originalDownload = page.waitForEvent('download');
        await page.locator('.actionSheet [data-id="download"]').click();
        const original = await originalDownload;
        assert.match(original.suggestedFilename(), /\.mkv$/);
        await original.cancel();
        passed('original movie Download action still works');

        await page.goto(state.url + '/web/#/home');
        const card = page.locator(`.card[data-id="${state.items['Other Movie']}"]`).first();
        await card.hover();
        await card.locator('[data-action="menu"]').click();
        await page.locator(rowSelector).waitFor();
        assert.equal(await page.locator(rowSelector).getAttribute('data-item-id'), state.items['Other Movie']);
        const otherEvent = page.waitForEvent('download');
        await page.locator(rowSelector).click();
        assert.equal((await otherEvent).suggestedFilename(), 'Other Movie.subtitles.zip');
        passed('library card popup downloads the correct movie');

        await page.locator('.actionSheet').waitFor({ state: 'detached' });
        await card.click({ button: 'right' });
        await page.locator(rowSelector).waitFor();
        assert.equal(await page.locator(rowSelector).getAttribute('data-item-id'), state.items['Other Movie']);
        passed('right-click popup uses the correct item');

        await details(page, 'No Subtitles');
        assert.equal(await page.locator(rowSelector).isDisabled(), true);
        passed('no-subtitle button is disabled');
        await details(page, 'Test Episode');
        const episodeEvent = page.waitForEvent('download');
        await page.locator(rowSelector).click();
        assert.equal((await episodeEvent).suggestedFilename(), 'Test Episode.subtitles.zip');
        passed('episode popup downloads subtitles');

        // Deliberately delay the response so the cancellation control is deterministic.
        await page.route('**/SubtitleDownload/Items/*/Download', async route => {
            await new Promise(resolve => setTimeout(resolve, 1500));
            await route.continue().catch(() => {});
        });
        await details(page, 'Subtitle Test');
        await page.locator(rowSelector).click();
        await page.locator('#subtitle-download-status').getByRole('button', { name: 'Cancel' }).click();
        await page.getByText('Subtitle download cancelled.', { exact: false }).waitFor();
        await page.unrouteAll({ behavior: 'wait' });
        await details(page, 'Subtitle Test');
        assert.equal(await page.locator(rowSelector).isEnabled(), true);
        passed('cancel restores the button and reports cancellation');

        const mobileContext = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
        const mobile = await mobileContext.newPage();
        await login(mobile);
        await details(mobile, 'Subtitle Test');
        const box = await mobile.locator(rowSelector).boundingBox();
        assert(box.x >= 0 && box.x + box.width <= 390 && box.y >= 0 && box.y + box.height <= 844, JSON.stringify(box));
        await mobile.screenshot({ path: path.join(output, 'download-subtitles-mobile.png') });
        passed('mobile popup keeps the action visible');
        await mobileContext.close();

        const blockedContext = await browser.newContext();
        const blocked = await blockedContext.newPage();
        await login(blocked, 'blocked');
        await blocked.goto(state.url + '/web/#/details?id=' + state.items['Subtitle Test']);
        await blocked.locator('.btnMoreCommands:visible').click();
        await blocked.locator('.actionSheet').waitFor();
        assert.equal(await blocked.locator(rowSelector).count(), 0);
        assert.equal(await blocked.locator('.actionSheet [data-id="download"]').count(), 0);
        passed('users without download permission see neither download action');
        await blockedContext.close();
        assert.deepEqual(errors, []);
        passed('no browser JavaScript errors');
        fs.writeFileSync(path.join(output, 'browser-results.json'), JSON.stringify(results, null, 2) + '\n');
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
