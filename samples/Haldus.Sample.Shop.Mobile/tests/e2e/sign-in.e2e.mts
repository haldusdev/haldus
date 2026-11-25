import assert from 'node:assert/strict';

import { button, field, role, text } from './support/chrome.mts';
import { en, KEYS, scene } from './support/harness.mts';

scene('a visitor without a session lands on the sign-in screen', async ({ shop, page }) => {
  await page.goto(`${shop.origin}/basket`);

  await page.waitForPath('/sign-in');
  await page.see(field(en.signIn.email));
  assert.equal(shop.sent('GET', '/me/baskets').length, 0);
});

scene('a wrong password shows the message of the server', async ({ shop, page }) => {
  shop.addAccount('ada@example.com', 'correct-horse-battery');
  await page.goto(`${shop.origin}/sign-in`);

  await page.fill(field(en.signIn.email), 'ada@example.com');
  await page.fill(field(en.signIn.password), 'wrong-password');
  await page.click(button(en.signIn.submit));

  assert.equal(await page.textOf(role('alert')), 'The e-mail address or password is incorrect.');
  assert.equal(await page.path(), '/sign-in');
  assert.equal(await page.storage(KEYS.accessToken), null);

  // Giriş isteği token taşımaz.
  const login = shop.last('POST', '/auth/login');
  assert.equal(login.headers.authorization, undefined);
  assert.deepEqual(login.body, { email: 'ada@example.com', password: 'wrong-password' });
});

scene('signing in stores the session and opens the product list', async ({ shop, page }) => {
  const account = shop.addAccount('ada@example.com', 'correct-horse-battery');
  shop.addProducts({ name: 'Kettle', price: 42.5 });
  await page.goto(`${shop.origin}/sign-in`);

  await page.fill(field(en.signIn.email), ' ada@example.com ');
  await page.fill(field(en.signIn.password), 'correct-horse-battery');
  await page.click(button(en.signIn.submit));

  await page.see(text('Kettle'));
  assert.equal(await page.path(), '/');

  // Oturum üç ayrı anahtarda saklanır (cihazdaki güvenli deponun boyut sınırı yüzünden).
  const accessToken = await page.storage(KEYS.accessToken);
  assert.match(accessToken ?? '', /^access-/);
  assert.match((await page.storage(KEYS.refreshToken)) ?? '', /^refresh-/);
  assert.deepEqual(JSON.parse((await page.storage(KEYS.account)) ?? '{}'), {
    accountId: account.id,
    email: 'ada@example.com',
    tenantId: account.tenantId,
    emailVerified: true,
  });
  assert.equal(shop.last('GET', '/products').headers.authorization, `Bearer ${accessToken}`);
  assert.deepEqual(shop.last('POST', '/auth/login').body, {
    email: 'ada@example.com',
    password: 'correct-horse-battery',
  });
});

scene('registration shows the field error, then opens the session', async ({ shop, page }) => {
  await page.goto(`${shop.origin}/sign-in`);
  await page.click(button(en.signIn.switchToRegister));
  await page.see(text(en.signIn.registerDescription));

  await page.fill(field(en.signIn.email), 'grace@example.com');
  await page.fill(field(en.signIn.password), 'short');
  await page.click(button(en.signIn.registerSubmit));

  await page.see(text('The password must be at least 8 characters.'));
  assert.equal(await page.attribute(field(en.signIn.password), 'aria-invalid'), 'true');

  await page.fill(field(en.signIn.password), 'a-long-enough-password');
  await page.click(button(en.signIn.registerSubmit));

  await page.see(text(en.products.empty));
  assert.equal(shop.accounts[0].email, 'grace@example.com');

  // Yeni hesabın e-postası doğrulanmamıştır: Hesap ekranı kodu ister.
  await page.click(role('tab', en.tabs.account));
  await page.see(text(en.verify.title));
});

scene('a forgotten password is reset with the mailed code', async ({ shop, page }) => {
  const account = shop.addAccount('ada@example.com', 'correct-horse-battery');
  await page.goto(`${shop.origin}/sign-in`);

  await page.click(button(en.signIn.forgot));
  await page.see(text(en.signIn.forgotDescription));
  await page.gone(field(en.signIn.password));

  await page.fill(field(en.signIn.email), 'ada@example.com');
  await page.click(button(en.signIn.forgotSubmit));
  await page.see(text(en.signIn.codeSent));

  await page.fill(field(en.signIn.code), 'WRONGCOD');
  await page.fill(field(en.signIn.newPassword), 'a-brand-new-password');
  await page.click(button(en.signIn.resetSubmit));
  assert.equal(await page.textOf(role('alert')), 'The code is invalid or has expired.');

  // Kod küçük harfle ve tireyle yazılabilir.
  await page.fill(field(en.signIn.code), 'k7m2-p9qx');
  await page.click(button(en.signIn.resetSubmit));

  await page.see(text(en.signIn.passwordReset));
  assert.equal(account.password, 'a-brand-new-password');
  assert.deepEqual(shop.last('POST', '/auth/password/reset').body, {
    email: 'ada@example.com',
    code: 'k7m2-p9qx',
    newPassword: 'a-brand-new-password',
  });

  await page.fill(field(en.signIn.password), 'a-brand-new-password');
  await page.click(button(en.signIn.submit));
  await page.see(text(en.products.empty));
});
