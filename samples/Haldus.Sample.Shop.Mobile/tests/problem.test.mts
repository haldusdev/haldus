import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { fieldErrors, problemDetail, problemMessage, problemStatus } from '@/lib/problem';

describe('problemDetail', () => {
  it('reads the detail the backend wrote', () => {
    assert.equal(
      problemDetail({ title: 'Forbidden', detail: 'Not allowed here.' }),
      'Not allowed here.'
    );
  });

  it('does not fall back to the untranslated title', () => {
    assert.equal(problemDetail({ status: 401, title: 'Unauthorized' }), undefined);
    assert.equal(problemDetail({ title: 'Unauthorized', detail: '' }), undefined);
    assert.equal(problemDetail({ detail: 7 }), undefined);
  });

  it('is undefined for anything that is not a problem', () => {
    assert.equal(problemDetail(new Error('The identity provider sign-in failed')), undefined);
    assert.equal(problemDetail('detail'), undefined);
    assert.equal(problemDetail(null), undefined);
  });
});

describe('problemStatus', () => {
  it('reads the status of a problem', () => {
    assert.equal(problemStatus({ status: 409, title: 'Conflict' }), 409);
  });

  it('is undefined for anything that carries no numeric status', () => {
    assert.equal(problemStatus({ status: '409' }), undefined);
    assert.equal(problemStatus({ title: 'Conflict' }), undefined);
    assert.equal(problemStatus(new TypeError('fetch failed')), undefined);
    assert.equal(problemStatus('409'), undefined);
    assert.equal(problemStatus(null), undefined);
    assert.equal(problemStatus(undefined), undefined);
  });
});

describe('problemMessage', () => {
  it('prefers the detail', () => {
    assert.equal(
      problemMessage({ title: 'Conflict', detail: 'Already shipped.' }, 'Oops'),
      'Already shipped.'
    );
  });

  it('falls back to the title when the detail is missing or empty', () => {
    assert.equal(problemMessage({ title: 'Conflict' }, 'Oops'), 'Conflict');
    assert.equal(problemMessage({ title: 'Conflict', detail: '' }, 'Oops'), 'Conflict');
    assert.equal(problemMessage({ title: 'Conflict', detail: 7 }, 'Oops'), 'Conflict');
  });

  it('falls back to the given text when there is neither', () => {
    assert.equal(problemMessage({ status: 500 }, 'Oops'), 'Oops');
    assert.equal(problemMessage({ title: '', detail: '' }, 'Oops'), 'Oops');
    assert.equal(problemMessage(null, 'Oops'), 'Oops');
    assert.equal(problemMessage('Conflict', 'Oops'), 'Oops');
  });

  it('does not show the message of a thrown Error', () => {
    assert.equal(problemMessage(new TypeError('fetch failed'), 'Oops'), 'Oops');
  });
});

describe('fieldErrors', () => {
  it('returns the messages by field', () => {
    assert.deepEqual(fieldErrors({ errors: { name: ['Required.'], price: ['Too low.'] } }), {
      name: ['Required.'],
      price: ['Too low.'],
    });
  });

  it('lowers the first letter of every segment of a field name', () => {
    assert.deepEqual(
      fieldErrors({
        errors: {
          Name: ['a'],
          'Items[0].Quantity': ['b'],
          'ShippingAddress.PostalCode': ['c'],
          sku: ['d'],
        },
      }),
      {
        name: ['a'],
        'items[0].quantity': ['b'],
        'shippingAddress.postalCode': ['c'],
        sku: ['d'],
      }
    );
  });

  it('keeps the rest of the name as it is', () => {
    assert.deepEqual(fieldErrors({ errors: { SKUCode: ['a'] } }), { sKUCode: ['a'] });
  });

  it('drops messages that are not text and fields that are not lists', () => {
    assert.deepEqual(fieldErrors({ errors: { name: ['Required.', 5, null], price: 'Too low.' } }), {
      name: ['Required.'],
    });
  });

  it('is empty when the error carries no field errors', () => {
    assert.deepEqual(fieldErrors({ title: 'Conflict', status: 409 }), {});
    assert.deepEqual(fieldErrors({ errors: 'nope' }), {});
    assert.deepEqual(fieldErrors({ errors: null }), {});
    assert.deepEqual(fieldErrors(new Error('boom')), {});
    assert.deepEqual(fieldErrors(undefined), {});
  });
});
