import { expect, test } from 'vitest';
import { ApiError } from './api';
import { failureMessages, MessageError, problemMessages, warningMessage } from './apiText';

const format = (value: number, digits?: number) => digits === undefined ? String(value) : value.toFixed(digits);

test('translates shown warnings by code and falls back to the API text', () => {
  expect(warningMessage({ code: 'startSnapped', message: 'Start moved 40 m…', params: { meters: 40 } }, format))
    .toEqual({ key: 'warning.startSnapped', params: { meters: '40' } });
  expect(warningMessage({ code: 'radiusExceeded', message: 'The edited route…', params: { edited: 1 } }, format))
    .toEqual({ key: 'warning.radiusExceededEdited' });
  expect(warningMessage({ code: 'providerWarning', message: 'There may be restrictions on some roads', params: { orsCode: 1 } }, format))
    .toEqual({ key: 'warning.roadRestrictions' });
  // Saved searches from before params existed keep their English text.
  expect(warningMessage({ code: 'startSnapped', message: 'Start moved 40 m to the nearest routable path.' }, format))
    .toEqual({ text: 'Start moved 40 m to the nearest routable path.' });
  expect(warningMessage({ code: 'somethingNew', message: 'New text' }, format)).toEqual({ text: 'New text' });
});

test('translates problems by code with minimum distance and retry hints', () => {
  const error = new ApiError({ code: 'targetTooShort', detail: 'The target is shorter…', minimumDistanceKm: 2 }, '30');
  expect(problemMessages(error, format)).toEqual([
    { key: 'errors.targetTooShort' }, { key: 'error.minimumDistance', params: { km: '2.00' } }, { key: 'error.retryAfter', params: { seconds: '30' } }]);
  const backtracking = new ApiError({ code: 'noUsableRoute', params: { avoidBacktracking: 1 } });
  expect(problemMessages(backtracking, format)).toEqual([{ key: 'errors.noUsableRoute' }, { key: 'errors.noUsableRouteBacktracking' }]);
  expect(problemMessages(new ApiError({ code: 'brandNew', detail: 'Raw detail' }), format)).toEqual([{ text: 'Raw detail' }]);
});

test('maps other failures to a fallback or their own message', () => {
  expect(failureMessages(new TypeError('Failed to fetch'), 'error.openSaved', format)).toEqual([{ key: 'error.openSaved' }]);
  expect(failureMessages(new MessageError({ key: 'error.savedNoRoutes' }), 'error.openSaved', format)).toEqual([{ key: 'error.savedNoRoutes' }]);
});
