'use strict';

// Reports contain only route identity and a bounded category, never raw prompts or credentials.
function diagnoseProvider(model, value, exitCode = null) {
  const text = String(value || '');
  const provider = model.providerId || model.provider || 'unknown';
  const adapter = model.adapter || model.provider || 'unknown';
  const id = model.cli || model.id || 'unknown';
  let code = 'PROVIDER_EXECUTION_FAILED';
  let suggestion = 'Review the retained local output and retry after checking the provider CLI.';
  if (/PERMISSION_MODE_UNSUPPORTED/.test(text)) { code = 'PERMISSION_MODE_UNSUPPORTED'; suggestion = 'Choose a supported permission mode or another adapter.'; }
  else if (/PROVIDER_MODEL_IDENTITY_CHANGED/.test(text)) { code = 'PROVIDER_MODEL_IDENTITY_CHANGED'; suggestion = 'The response identified a different model. Verify the exact model ID and provider aliases before retrying.'; }
  else if (/API_CONFIGURATION_INVALID|CLI_CONFIGURATION_INVALID/.test(text)) { code = 'PROVIDER_CONFIGURATION_INVALID'; suggestion = 'Review the endpoint, protocol and declared CLI arguments for this connection.'; }
  else if (/unexpected argument|unknown (?:option|flag)|unrecognized (?:argument|option)|flag provided but not defined|unsupported.*format|incompatible.*protocol/i.test(text)) { code = 'PROVIDER_CLI_INCOMPATIBLE'; suggestion = 'The CLI interface may have changed. Check its version and update the Planwharf adapter.'; }
  else if (/PROVIDER_PROTOCOL_CHANGED|missing or invalid structured|unreadable.*(?:metadata|response)|no recognized response/i.test(text)) { code = 'PROVIDER_PROTOCOL_CHANGED'; suggestion = 'The response contract could not be verified. Review CLI versions and the adapter parser.'; }
  else if (/429|quota|credit|spending.limit|rate.limit/i.test(text)) { code = 'PROVIDER_USAGE_LIMIT'; suggestion = 'Check provider quota and your configured usage policy.'; }
  else if (/401|unauthorized|authentication|not logged in|invalid.*(?:key|token)/i.test(text)) { code = 'PROVIDER_AUTHENTICATION_FAILED'; suggestion = 'Sign in to this provider again, then refresh models.'; }
  else if (/invalid model selection|model.*(?:not found|unsupported|unavailable|does not exist|not recognized|unknown)|access.*model|model.*access/i.test(text)) { code = 'PROVIDER_MODEL_UNAVAILABLE'; suggestion = 'Refresh models and verify access to this exact provider/model ID.'; }
  else if (/executable not found|ENOENT/i.test(text)) { code = 'PROVIDER_CLI_MISSING'; suggestion = 'Install or configure this adapter executable.'; }
  else if (/timeout|ETIMEDOUT|ENOTFOUND|ECONN|certificate|network/i.test(text)) { code = 'PROVIDER_CONNECTION_FAILED'; suggestion = 'Check connectivity and certificates before retrying.'; }
  const message = `${code}: ${provider} / ${id}. ${suggestion}`;
  const report = `Planwharf provider integration report\n\nCategory: ${code}\nProvider: ${provider}\nAdapter: ${adapter}\nExact model: ${id}\nExit code: ${exitCode ?? 'unavailable'}\n\nCLI version: [fill in locally]\nExpected behavior: [describe]\nObserved behavior: [describe without credentials or personal project data]\n\nRaw logs and prompts are intentionally excluded.`;
  const reportUrl = 'https://github.com/benitz94/Planwharf/issues/new?title=' + encodeURIComponent(code + ': ' + adapter) + '&body=' + encodeURIComponent(report);
  return { code, provider, adapter, model: id, exitCode, message, suggestion, report, reportUrl };
}
module.exports = { diagnoseProvider };
