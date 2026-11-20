'use strict';

// Official status pages. A company is "up" when its page says operational and names no open incident.
const PAGES = [
  { id: 'openai', name: 'OpenAI', url: 'https://status.openai.com' },
  { id: 'anthropic', name: 'Anthropic', url: 'https://status.claude.com' },
  { id: 'xai', name: 'xAI', url: 'https://status.x.ai' },
  { id: 'google', name: 'Google', url: 'https://status.cloud.google.com' }
];

async function readStatus(page, fetchImpl) {
  try {
    const response = await fetchImpl(page.url, { signal: AbortSignal.timeout(8000) });
    if (!response.ok) return { ...page, up: false, note: 'Status page did not answer.' };
    const text = (await response.text()).toLowerCase();
    const incident = /major outage|partial outage|investigating|degraded performance/.test(text) && !/no incidents|all systems operational|operational/.test(text);
    return { ...page, up: !incident, note: incident ? 'An incident is open.' : 'Operational.' };
  } catch {
    return { ...page, up: false, note: 'Could not reach the status page.' };
  }
}

async function providerStatus(fetchImpl = globalThis.fetch) {
  return Promise.all(PAGES.map(page => readStatus(page, fetchImpl)));
}

module.exports = { providerStatus, PAGES };
