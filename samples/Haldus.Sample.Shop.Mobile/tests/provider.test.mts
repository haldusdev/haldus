import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';

import { answerPrompt, prompts, redirects } from './stubs/expo-auth-session.ts';
import { calls, json, respondWith, text } from './support/fetch.ts';

import {
  promptProviderSignIn,
  prepareProviderSignIn,
  ProviderRejectionError,
  ProviderSignInError,
  providerRedirectUri,
  redeemProviderCode,
  renewProviderTokens,
} from '@/lib/provider';

const provider = {
  authority: 'https://id.test/realms/shop',
  clientId: 'shop-mobile',
  scopes: 'openid email offline_access',
};
const metadata = {
  issuer: provider.authority,
  authorization_endpoint: 'https://id.test/connect/authorize',
  token_endpoint: 'https://id.test/connect/token',
};
const link = { clientId: 'shop-mobile', tokenEndpoint: 'https://id.test/connect/token' };

describe('the address of the provider', () => {
  it('refuses plain http outside the development addresses', async () => {
    respondWith(() => json(200, metadata));

    await assert.rejects(
      prepareProviderSignIn({ ...provider, authority: 'http://id.test/realms/shop' }),
      ProviderSignInError
    );
    assert.equal(calls.length, 0);
  });

  it('accepts plain http for the device itself and the emulator host', async () => {
    for (const authority of [
      'http://localhost:8080/realms/shop',
      'http://10.0.2.2:8080/realms/shop',
    ]) {
      respondWith(() => json(200, { ...metadata, issuer: authority }));

      const prompt = await prepareProviderSignIn({ ...provider, authority });
      assert.equal(prompt.tokenEndpoint, metadata.token_endpoint);
    }
  });

  it('refuses a discovery document that belongs to another issuer', async () => {
    respondWith(() => json(200, { ...metadata, issuer: 'https://evil.test/realms/shop' }));

    await assert.rejects(prepareProviderSignIn(provider), ProviderSignInError);
  });
});

function unreachable(): Response {
  throw new TypeError('Network request failed');
}

describe('provider', () => {
  beforeEach(() => {
    answerPrompt({ type: 'cancel' });
    respondWith(() => json(200, metadata));
  });

  describe('providerRedirectUri', () => {
    it('returns to the sign-in screen through the scheme of the app', () => {
      assert.equal(providerRedirectUri(), 'haldus-shop://sign-in');
      // Derleme dışında (Expo Go, web) adresi kütüphane kurar; yol yine giriş ekranınınkidir.
      assert.deepEqual(redirects, [{ native: 'haldus-shop://sign-in', path: 'sign-in' }]);
    });
  });

  describe('prepareProviderSignIn', () => {
    it('discovers the endpoints of the provider from its authority', async () => {
      const prompt = await prepareProviderSignIn(provider);

      assert.equal(calls.length, 1);
      assert.equal(calls[0].url, 'https://id.test/realms/shop/.well-known/openid-configuration');
      assert.equal(calls[0].method, 'GET');
      assert.equal(prompt.authorizationEndpoint, metadata.authorization_endpoint);
      assert.equal(prompt.tokenEndpoint, metadata.token_endpoint);
    });

    it('accepts an authority written with a trailing slash', async () => {
      await prepareProviderSignIn({ ...provider, authority: `${provider.authority}//` });

      assert.equal(calls[0].url, 'https://id.test/realms/shop/.well-known/openid-configuration');
    });

    it('builds the request with the client, the scopes and the redirect address', async () => {
      const { request } = await prepareProviderSignIn({
        ...provider,
        scopes: '  openid   email\toffline_access ',
      });

      assert.equal(request.clientId, 'shop-mobile');
      assert.deepEqual(request.scopes, ['openid', 'email', 'offline_access']);
      assert.equal(request.redirectUri, 'haldus-shop://sign-in');
    });

    it('prepares a new state and verifier for every attempt', async () => {
      const first = await prepareProviderSignIn(provider);
      const second = await prepareProviderSignIn(provider);

      assert.notEqual(first.request.state, second.request.state);
      assert.notEqual(first.request.codeVerifier, second.request.codeVerifier);
    });

    it('sends no token of the shop to the provider', async () => {
      await prepareProviderSignIn(provider);

      assert.equal(calls[0].headers.get('Authorization'), null);
    });

    for (const [name, respond] of [
      ['cannot be reached', unreachable],
      ['answers with a page that is not JSON', () => text(502, '<html>Bad Gateway</html>', 'Bad')],
      ['answers with an error body', () => json(404, { message: 'Realm does not exist' })],
      ['names no token endpoint', () => json(200, { ...metadata, token_endpoint: undefined })],
      [
        'names no authorization endpoint',
        () => json(200, { ...metadata, authorization_endpoint: '' }),
      ],
    ] as const) {
      it(`fails when the provider ${name}`, async () => {
        respondWith(respond);

        await assert.rejects(prepareProviderSignIn(provider), ProviderSignInError);
      });
    }
  });

  describe('promptProviderSignIn', () => {
    it('opens the authorization endpoint with the prepared request', async () => {
      const prompt = await prepareProviderSignIn(provider);
      answerPrompt({ type: 'dismiss' });

      const result = await promptProviderSignIn(prompt);

      assert.deepEqual(result, { type: 'dismiss' });
      assert.equal(prompts.length, 1);
      assert.equal(prompts[0].request, prompt.request);
      assert.deepEqual(prompts[0].discovery, {
        authorizationEndpoint: metadata.authorization_endpoint,
      });
    });
  });

  describe('redeemProviderCode', () => {
    const granted = { access_token: 'idp-access-1', refresh_token: 'idp-refresh-1' };

    it('exchanges the code with the verifier of the request and no secret', async () => {
      const prompt = await prepareProviderSignIn(provider);
      respondWith(() => json(200, granted));

      await redeemProviderCode(provider, prompt, 'code-1');

      assert.equal(calls.length, 1);
      assert.equal(calls[0].url, 'https://id.test/connect/token');
      assert.equal(calls[0].method, 'POST');
      assert.equal(calls[0].headers.get('Content-Type'), 'application/x-www-form-urlencoded');
      assert.equal(calls[0].headers.get('Authorization'), null);
      assert.deepEqual(calls[0].body, {
        grant_type: 'authorization_code',
        client_id: 'shop-mobile',
        code: 'code-1',
        code_verifier: prompt.request.codeVerifier,
        redirect_uri: 'haldus-shop://sign-in',
      });
    });

    it('returns the tokens with what a later renewal needs', async () => {
      const prompt = await prepareProviderSignIn(provider);
      respondWith(() => json(200, granted));

      assert.deepEqual(await redeemProviderCode(provider, prompt, 'code-1'), {
        accessToken: 'idp-access-1',
        refreshToken: 'idp-refresh-1',
        link,
      });
    });

    it('reports a provider that issues no refresh token', async () => {
      const prompt = await prepareProviderSignIn(provider);
      respondWith(() => json(200, { access_token: 'idp-access-1' }));

      const tokens = await redeemProviderCode(provider, prompt, 'code-1');

      assert.equal(tokens.refreshToken, null);
    });

    for (const [name, respond] of [
      ['rejects the code', () => json(400, { error: 'invalid_grant' })],
      ['answers without an access token', () => json(200, { token_type: 'Bearer' })],
      ['answers with a server error', () => json(500, { message: 'Internal Server Error' })],
      ['answers with a page that is not JSON', () => text(502, '<html></html>', 'Bad Gateway')],
      ['cannot be reached', unreachable],
    ] as const) {
      it(`fails when the provider ${name}`, async () => {
        const prompt = await prepareProviderSignIn(provider);
        respondWith(respond);

        await assert.rejects(redeemProviderCode(provider, prompt, 'code-1'), ProviderSignInError);
      });
    }

    it('refuses a request that lost its verifier', async () => {
      const prompt = await prepareProviderSignIn(provider);
      prompt.request.codeVerifier = undefined;
      respondWith(() => json(200, granted));

      await assert.rejects(redeemProviderCode(provider, prompt, 'code-1'), ProviderSignInError);
      assert.equal(calls.length, 0);
    });
  });

  describe('renewProviderTokens', () => {
    it('asks the token endpoint of the session with the refresh token', async () => {
      respondWith(() =>
        json(200, { access_token: 'idp-access-2', refresh_token: 'idp-refresh-2' })
      );

      const tokens = await renewProviderTokens(link, 'idp-refresh-1');

      assert.deepEqual(tokens, { accessToken: 'idp-access-2', refreshToken: 'idp-refresh-2' });
      assert.equal(calls.length, 1);
      assert.equal(calls[0].url, 'https://id.test/connect/token');
      assert.equal(calls[0].method, 'POST');
      assert.equal(calls[0].headers.get('Authorization'), null);
      assert.deepEqual(calls[0].body, {
        grant_type: 'refresh_token',
        client_id: 'shop-mobile',
        refresh_token: 'idp-refresh-1',
      });
    });

    it('keeps the refresh token when the provider does not rotate it', async () => {
      respondWith(() => json(200, { access_token: 'idp-access-2' }));

      assert.deepEqual(await renewProviderTokens(link, 'idp-refresh-1'), {
        accessToken: 'idp-access-2',
        refreshToken: 'idp-refresh-1',
      });
    });

    for (const code of [
      'invalid_grant',
      'invalid_client',
      'invalid_request',
      'unauthorized_client',
      'unsupported_grant_type',
      'invalid_scope',
    ]) {
      it(`reports ${code} as a rejection`, async () => {
        respondWith(() => json(400, { error: code, error_description: 'No.' }));

        await assert.rejects(renewProviderTokens(link, 'idp-refresh-1'), ProviderRejectionError);
      });
    }

    // Kesin ret olmayan her şey: oturum bunlarla kapanmaz (api-fetch-provider.test.mts).
    for (const [name, respond] of [
      ['a server error code', () => json(500, { error: 'server_error' })],
      ['an overloaded provider', () => json(503, { error: 'temporarily_unavailable' })],
      ['a rate limit', () => json(429, { error: 'slow_down' })],
      ['a server error without a code', () => json(500, { message: 'Internal Server Error' })],
      ['a proxy error page', () => text(502, '<html>Bad Gateway</html>', 'Bad Gateway')],
      ['a JSON null', () => json(200, null)],
      ['a response without an access token', () => json(200, { token_type: 'Bearer' })],
      ['a network error', unreachable],
    ] as const) {
      it(`does not report ${name} as a rejection`, async () => {
        respondWith(respond);

        await assert.rejects(renewProviderTokens(link, 'idp-refresh-1'), (error) => {
          assert.equal(error instanceof ProviderRejectionError, false);
          return true;
        });
      });
    }
  });
});
