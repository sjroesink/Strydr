import { ApiError } from './api';
import type { Message, MessageKey } from './i18n';
import { en } from './locales/en';
import type { RouteWarning } from './types';

type Format = (value: number, digits?: number) => string;

// A locally raised failure whose text is already a translatable message.
export class MessageError extends Error {
  constructor(public readonly content: Message) { super('text' in content ? content.text : content.key); }
}

const known = (key: string): key is MessageKey => key in en;

// Shown warnings are translated by code; anything unknown or without its numbers keeps the API's English text.
export function warningMessage(warning: RouteWarning, format: Format): Message {
  const params = warning.params ?? undefined;
  switch (warning.code) {
    case 'drawnPointSnapped': case 'startSnapped': case 'endSnapped':
      return params?.meters !== undefined
        ? { key: `warning.${warning.code}` as MessageKey, params: { meters: format(params.meters, 0) } } : { text: warning.message };
    case 'fewerRoutes':
      return params?.found !== undefined && params.requested !== undefined
        ? { key: 'warning.fewerRoutes', params: { found: format(params.found, 0), requested: format(params.requested, 0) } } : { text: warning.message };
    case 'radiusExceeded':
      if (params?.edited) return { key: 'warning.radiusExceededEdited' };
      return params?.km !== undefined && params.radiusKm !== undefined
        ? { key: 'warning.radiusExceeded', params: { km: format(params.km, 2), radiusKm: format(params.radiusKm, 2) } } : { text: warning.message };
    case 'partialSearch': case 'greenDataUnavailable': case 'woodlandNotReached':
      return { key: `warning.${warning.code}` as MessageKey };
    case 'providerWarning':
      return params?.orsCode === 1 ? { key: 'warning.roadRestrictions' } : { text: warning.message };
    default:
      return { text: warning.message };
  }
}

export function problemMessages(error: ApiError, format: Format): Message[] {
  const { problem } = error;
  const key = `errors.${problem.code}`;
  const messages: Message[] = problem.code === 'apiUnreachable' ? [{ key: 'error.unreachable' }]
    : problem.code && known(key) ? [{ key }]
    : !problem.detail && !problem.title ? [{ key: 'error.request' }]
    : [{ text: error.message }];
  if (problem.code === 'noUsableRoute' && problem.params?.avoidBacktracking) messages.push({ key: 'errors.noUsableRouteBacktracking' });
  if (problem.minimumDistanceKm !== undefined) messages.push({ key: 'error.minimumDistance', params: { km: format(problem.minimumDistanceKm, 2) } });
  if (error.retryAfter) messages.push({ key: 'error.retryAfter', params: { seconds: error.retryAfter } });
  return messages;
}

export function failureMessages(failure: unknown, fallback: MessageKey, format: Format): Message[] {
  if (failure instanceof ApiError) return problemMessages(failure, format);
  if (failure instanceof MessageError) return [failure.content];
  return [{ key: fallback }];
}
