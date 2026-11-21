'use strict';

// Captain-facing Telegram and Ollama voice. English and Italian.
// Command names stay the same. Technical notes are opt-in.

const SUPPORTED_LANGUAGES = ['en', 'it'];

const ALERT_LABELS = {
  en: {
    done: 'Done ✅',
    failed: 'Failed ❌',
    needs_owner: 'Needs your attention 🔔',
    stopped: 'Stopped ⏹',
    started: 'Started ▶️',
    resumed: 'Resumed ▶️',
    blocked: 'Blocked ⚠️',
    verify: 'Verify started 🔎',
    switched: 'Switched model 🔁',
    restarted: 'Step restarted 🔁',
    updated: 'Updated •'
  },
  it: {
    done: 'Fatto ✅',
    failed: 'Non riuscito ❌',
    needs_owner: 'Serve il tuo intervento 🔔',
    stopped: 'Fermo ⏹',
    started: 'Partito ▶️',
    resumed: 'Ripreso ▶️',
    blocked: 'Bloccato ⚠️',
    verify: 'Controllo avviato 🔎',
    switched: 'Modello cambiato 🔁',
    restarted: 'Passo riavviato 🔁',
    updated: 'Aggiornato •'
  }
};

const PLAIN_ALERTS = {
  en: {
    done: 'The work is finished.',
    failed: 'The work did not succeed. The crew has stopped.',
    needs_owner: 'The crew is waiting for you.',
    stopped: 'The crew is paused.',
    started: 'The crew has started.',
    resumed: 'The crew is working again.',
    blocked: 'The crew cannot move until Planwharf has a worker and a place to work.',
    verify: 'A different model is checking the work.',
    switched: 'The crew changed who is doing this step.',
    restarted: 'The same step started again.',
    updated: 'The project changed.'
  },
  it: {
    done: 'Il lavoro è finito.',
    failed: 'Il lavoro non è riuscito. L\'equipaggio si è fermato.',
    needs_owner: 'L\'equipaggio aspetta te.',
    stopped: 'L\'equipaggio è in pausa.',
    started: 'L\'equipaggio è partito.',
    resumed: 'L\'equipaggio lavora di nuovo.',
    blocked: 'L\'equipaggio non può muoversi finché Planwharf non ha un lavoratore e un posto dove lavorare.',
    verify: 'Un altro modello sta controllando il lavoro.',
    switched: 'L\'equipaggio ha cambiato chi esegue questo passo.',
    restarted: 'Lo stesso passo è ricominciato.',
    updated: 'Il progetto è cambiato.'
  }
};

const STATE_LABELS = {
  en: {
    Working: 'Working',
    Done: 'Done',
    Failed: 'Failed',
    Stopped: 'Stopped',
    'Waiting on you': 'Waiting on you',
    'Blocked (no usage)': 'Blocked (no usage)',
    Idle: 'Idle'
  },
  it: {
    Working: 'Al lavoro',
    Done: 'Fatto',
    Failed: 'Non riuscito',
    Stopped: 'Fermo',
    'Waiting on you': 'In attesa di te',
    'Blocked (no usage)': 'Bloccato (senza utilizzo)',
    Idle: 'A riposo'
  }
};

const LANGUAGE_NAMES = { en: { en: 'English', it: 'Inglese' }, it: { en: 'Italian', it: 'Italiano' } };

function normalizeLanguage(value) {
  const text = String(value || '').trim().toLowerCase();
  const base = text.split(/[-_]/)[0];
  if (base === 'en' || text === 'english' || text === 'inglese') return 'en';
  if (base === 'it' || text === 'italian' || text === 'italiano') return 'it';
  return null;
}

function languageOrDefault(value) {
  return normalizeLanguage(value) || 'en';
}

function languageName(language, nameOf) {
  const lang = languageOrDefault(language);
  const target = languageOrDefault(nameOf || lang);
  return LANGUAGE_NAMES[target][lang];
}

function fill(template, vars) {
  return String(template || '').replace(/\{([a-zA-Z0-9]+)\}/g, (_, key) => (
    vars && vars[key] != null ? String(vars[key]) : ''
  ));
}

const CATALOG = {
  pairing: {
    en: [
      '🏴‍☠️ Ahoy, Captain!',
      '',
      '<b>Harbor Connected</b>',
      'Planwharf connected and standing by.',
      'You can now use the deck buttons or commands:',
      '• /status — projects and progress',
      '• /stop &lt;project&gt; — stop the crew (requires confirmation)',
      '• /resume &lt;project&gt; — resume work',
      '• /details — the short technical note, only when you ask',
      '• /language — English or Italiano'
    ].join('\n'),
    it: [
      '🏴‍☠️ Ahoy, Capitano!',
      '',
      '<b>Porto collegato</b>',
      'Planwharf è collegato e in attesa.',
      'Puoi usare i pulsanti di coperta o i comandi:',
      '• /status — progetti e avanzamento',
      '• /stop &lt;progetto&gt; — ferma l\'equipaggio (serve conferma)',
      '• /resume &lt;progetto&gt; — riprendi il lavoro',
      '• /details — la nota tecnica breve, solo se la chiedi',
      '• /language — English o Italiano'
    ].join('\n')
  },
  help: {
    en: [
      '🏴‍☠️ Ahoy, Captain!',
      '',
      '<b>Harbor Guide</b>',
      'Use the deck buttons, or:',
      '• /status — projects and progress',
      '• /stop &lt;project&gt; — stop the crew (requires confirmation)',
      '• /resume &lt;project&gt; — resume work',
      '• /details — the short technical note from the last problem',
      '• /language — English or Italiano',
      'You can also ask in plain words. The buttons do the same work as the commands.'
    ].join('\n'),
    it: [
      '🏴‍☠️ Ahoy, Capitano!',
      '',
      '<b>Guida del porto</b>',
      'Usa i pulsanti di coperta, oppure:',
      '• /status — progetti e avanzamento',
      '• /stop &lt;progetto&gt; — ferma l\'equipaggio (serve conferma)',
      '• /resume &lt;progetto&gt; — riprendi il lavoro',
      '• /details — la nota tecnica breve dell\'ultimo problema',
      '• /language — English o Italiano',
      'Puoi anche chiedere con parole semplici. I pulsanti fanno lo stesso lavoro dei comandi.'
    ].join('\n')
  },
  stopConfirm: {
    en: [
      '🏴‍☠️ Ahoy, Captain!',
      '',
      '<b>Confirm Stop Order</b>',
      '⏹ Stop the crew for "{name}"? Reply "yes" to confirm.',
      'The crew pauses only after you answer yes.'
    ].join('\n'),
    it: [
      '🏴‍☠️ Ahoy, Capitano!',
      '',
      '<b>Conferma la fermata</b>',
      '⏹ Fermo l\'equipaggio di "{name}"? Rispondi "yes" o "sì" per confermare.',
      'L\'equipaggio si ferma solo dopo la tua risposta.'
    ].join('\n')
  },
  stopConfirmTap: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Confirm Stop Order</b>\n⏹ Stop this crew? Reply "yes" to confirm or tap below.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Conferma la fermata</b>\n⏹ Fermo questo equipaggio? Rispondi "yes" o "sì", oppure tocca sotto.'
  },
  crewStopped: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Crew Stopped</b>\n⏹ The crew for "{name}" has been stopped.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Equipaggio fermo</b>\n⏹ L\'equipaggio di "{name}" è stato fermato.'
  },
  stopCancelled: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Stop Cancelled</b>\nStop cancelled. Crew will continue work.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Fermata annullata</b>\nFermata annullata. L\'equipaggio continua il lavoro.'
  },
  couldNotCompleteStop: {
    en: [
      '🏴‍☠️ Ahoy, Captain!',
      '',
      '<b>Harbor Notice</b>',
      '⚠️ Planwharf could not complete that.',
      'The crew is still working. Nothing was changed.',
      'Ask for technical details with /details.'
    ].join('\n'),
    it: [
      '🏴‍☠️ Ahoy, Capitano!',
      '',
      '<b>Avviso dal porto</b>',
      '⚠️ Planwharf non è riuscito a fermare l\'equipaggio.',
      'L\'equipaggio sta ancora lavorando. Non è cambiato nulla.',
      'Chiedi i dettagli tecnici con /details.'
    ].join('\n')
  },
  couldNotCompleteResume: {
    en: [
      '🏴‍☠️ Ahoy, Captain!',
      '',
      '<b>Harbor Notice</b>',
      '⚠️ Planwharf could not complete that.',
      'The crew is still paused. Nothing was changed.',
      'Ask for technical details with /details.'
    ].join('\n'),
    it: [
      '🏴‍☠️ Ahoy, Capitano!',
      '',
      '<b>Avviso dal porto</b>',
      '⚠️ Planwharf non è riuscito a far ripartire l\'equipaggio.',
      'L\'equipaggio è ancora in pausa. Non è cambiato nulla.',
      'Chiedi i dettagli tecnici con /details.'
    ].join('\n')
  },
  couldNotReadProjects: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Notice</b>\n⚠️ Planwharf could not read the projects. Try again.\nAsk for technical details with /details.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Avviso dal porto</b>\n⚠️ Planwharf non riesce a leggere i progetti. Riprova.\nChiedi i dettagli tecnici con /details.'
  },
  stopNone: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Stop Order</b>\nNo projects available to stop.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Ordine di fermata</b>\nNon ci sono progetti da fermare.'
  },
  stopSpecify: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Stop Order</b>\nPlease specify a project: /stop &lt;name&gt;\n<b>Projects in port:</b> {names}',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Ordine di fermata</b>\nIndica un progetto: /stop &lt;nome&gt;\n<b>Progetti in porto:</b> {names}'
  },
  resumeNone: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Resume Order</b>\nNo projects available to resume.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Ordine di ripresa</b>\nNon ci sono progetti da riprendere.'
  },
  resumeSpecify: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Resume Order</b>\nPlease specify a project: /resume &lt;name&gt;\n<b>Projects in port:</b> {names}',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Ordine di ripresa</b>\nIndica un progetto: /resume &lt;nome&gt;\n<b>Progetti in porto:</b> {names}'
  },
  crewResumed: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Crew Resumed</b>\n▶️ The crew for "{name}" has resumed.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Equipaggio ripartito</b>\n▶️ L\'equipaggio di "{name}" è ripartito.'
  },
  memoryCleared: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Logbook Cleared</b>\nThis chat memory is clear. Commands are unchanged.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Giornale di bordo pulito</b>\nLa memoria di questa chat è pulita. I comandi restano gli stessi.'
  },
  unrecognized: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Unrecognized Command</b>\nUnrecognized command. Use /status, /stop &lt;project&gt;, or /resume &lt;project&gt;.\nOr ask in plain words. /details shows the last technical note.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Comando non riconosciuto</b>\nComando non riconosciuto. Usa /status, /stop &lt;progetto&gt; o /resume &lt;progetto&gt;.\nOppure chiedi con parole semplici. /details mostra l\'ultima nota tecnica.'
  },
  attachments: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Notice</b>\n📎 Attachments are not accepted. Use the deck buttons.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Avviso dal porto</b>\n📎 Gli allegati non sono accettati. Usa i pulsanti di coperta.'
  },
  statusEmpty: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Status</b>\nNo projects in port.\nWhen a project is running, its step appears here.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Stato del porto</b>\nNessun progetto in porto.\nQuando un progetto è in corso, il suo passo compare qui.'
  },
  statusHeader: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Status</b>\nHere is where every project stands.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Stato del porto</b>\nEcco a che punto è ogni progetto.'
  },
  progressStep: {
    en: 'The crew is on step {current} of {total}.',
    it: 'L\'equipaggio è al passo {current} di {total}.'
  },
  detailsNone: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Technical details</b>\nNothing technical is waiting. The last command finished without an error.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Dettagli tecnici</b>\nNon c\'è nulla di tecnico in attesa. L\'ultimo comando è finito senza errori.'
  },
  details: {
    en: [
      '🏴‍☠️ Ahoy, Captain!',
      '',
      '<b>Technical details</b>',
      'Code: {code}',
      'Note: {note}',
      'File names, paths and secrets are left out.'
    ].join('\n'),
    it: [
      '🏴‍☠️ Ahoy, Capitano!',
      '',
      '<b>Dettagli tecnici</b>',
      'Codice: {code}',
      'Nota: {note}',
      'Nomi di file, percorsi e segreti sono esclusi.'
    ].join('\n')
  },
  languageShow: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Language</b>\nMessages are in {languageName}.\nSend /language en or /language it.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Lingua</b>\nI messaggi sono in {languageName}.\nInvia /language en o /language it.'
  },
  languageSet: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Language</b>\nMessages are now in {languageName}. Commands stay the same.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Lingua</b>\nI messaggi ora sono in {languageName}. I comandi restano gli stessi.'
  },
  languageUnknown: {
    en: '🏴‍☠️ Ahoy, Captain!\n\n<b>Language</b>\nPlanwharf knows English and Italian.\nSend /language en or /language it.',
    it: '🏴‍☠️ Ahoy, Capitano!\n\n<b>Lingua</b>\nPlanwharf conosce inglese e italiano.\nInvia /language en o /language it.'
  },
  companionOffline: {
    en: [
      '🏴‍☠️ Ahoy, Captain!',
      '',
      '<b>Harbor Companion Offline</b>',
      '⚠️ The local model is not answering right now. {reasonLine}',
      'Deck commands still work:',
      '• /status',
      '• /stop &lt;project&gt;',
      '• /resume &lt;project&gt;',
      'Ask for technical details with /details.'
    ].join('\n'),
    it: [
      '🏴‍☠️ Ahoy, Capitano!',
      '',
      '<b>Compagno di porto assente</b>',
      '⚠️ Il modello locale non risponde in questo momento. {reasonLine}',
      'I comandi di coperta funzionano ancora:',
      '• /status',
      '• /stop &lt;progetto&gt;',
      '• /resume &lt;progetto&gt;',
      'Chiedi i dettagli tecnici con /details.'
    ].join('\n')
  },
  conversationExtra: {
    en: 'Speak warmly, like a first mate helping a captain who does not write code. Say what the progress means in everyday words. When something goes wrong, say what the captain can do next. Technical details stay out unless the captain asks.',
    it: 'The captain selected Italian. Answer in Italian. Parla con parole semplici, come un nostromo che aiuta un capitano che non scrive codice. Spiega l\'avanzamento come passi, non come codice. Se qualcosa non va, di\' cosa può fare il capitano. I dettagli tecnici restano fuori finché il capitano non li chiede.'
  }
};

function say(language, key, vars) {
  const lang = languageOrDefault(language);
  const table = CATALOG[key];
  if (!table) return '';
  return fill(table[lang] || table.en, vars);
}

function telegramAlertLabel(language, type) {
  const lang = languageOrDefault(language);
  return ALERT_LABELS[lang][type] || ALERT_LABELS[lang].updated;
}

function plainAlert(language, type) {
  const lang = languageOrDefault(language);
  return PLAIN_ALERTS[lang][type] || PLAIN_ALERTS[lang].updated;
}

function stateLabel(language, state) {
  const lang = languageOrDefault(language);
  return STATE_LABELS[lang][state] || state;
}

function messageKeys() {
  return Object.keys(CATALOG);
}

const FRAGMENT_KEYS = new Set(['progressStep', 'conversationExtra']);

function isFragment(key) {
  return FRAGMENT_KEYS.has(key);
}

function commandVerb(text) {
  return String(text || '').trim().replace(/^\/([a-z0-9]+)(?:@[A-Za-z0-9_]+)?/i, (_, verb) => '/' + verb.toLowerCase());
}

function isDetailsRequest(text) {
  const value = String(text || '').trim().toLowerCase();
  const command = commandVerb(value);
  if (command === '/details' || command.startsWith('/details ') || command === '/dettagli' || command.startsWith('/dettagli ')) return true;
  return new Set([
    'details',
    'show details',
    'technical details',
    'show technical details',
    'technical note',
    'show technical note',
    'dettagli',
    'mostra dettagli',
    'mostra i dettagli',
    'dettagli tecnici',
    'mostra dettagli tecnici',
    'nota tecnica'
  ]).has(value);
}

function parseLanguageCommand(text) {
  const match = String(text || '').trim().match(/^\/(?:language|lingua)(?:@[A-Za-z0-9_]+)?(?:\s+(\S+))?$/i);
  if (!match) return null;
  if (!match[1]) return { action: 'show' };
  const language = normalizeLanguage(match[1]);
  if (!language) return { action: 'unknown', raw: match[1] };
  return { action: 'set', language };
}

function reasonLine(language, reason) {
  const lang = languageOrDefault(language);
  const code = String(reason || '');
  const lines = lang === 'it'
    ? {
      timeout: 'Ha impiegato troppo tempo a rispondere.',
      unreachable: 'L\'aiutante su questo computer non ha risposto.',
      empty: 'Ha risposto senza testo.',
      http: 'Ha rifiutato la domanda.',
      default: 'L\'aiutante su questo computer non ha risposto.'
    }
    : {
      timeout: 'It took too long to answer.',
      unreachable: 'The helper on this computer did not answer.',
      empty: 'It answered with nothing.',
      http: 'It refused the question.',
      default: 'The helper on this computer did not answer.'
    };
  if (code === 'timeout') return lines.timeout;
  if (code === 'unreachable') return lines.unreachable;
  if (code === 'empty') return lines.empty;
  if (code.startsWith('HTTP')) return lines.http;
  return lines.default;
}

function companionOffline(language, reason) {
  return say(language, 'companionOffline', { reasonLine: reasonLine(language, reason) });
}

const ALLOWED_TAGS = new Set(['b', 'i', 'u', 's', 'code', 'pre', 'a']);

function telegramReadable(text) {
  const reasons = [];
  const value = String(text ?? '');
  if (!value.trim()) reasons.push('empty');
  if (value.length > 4096) reasons.push('too-long');
  if (/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(value)) reasons.push('control');
  const tags = [];
  const re = /<\/?([a-zA-Z0-9-]+)(?:\s[^<>]*)?>/g;
  let match;
  let last = 0;
  while ((match = re.exec(value))) {
    const between = value.slice(last, match.index);
    if (between.includes('<') || between.includes('>')) reasons.push('raw-bracket');
    last = match.index + match[0].length;
    const name = match[1].toLowerCase();
    if (!ALLOWED_TAGS.has(name)) reasons.push(`tag-${name}`);
    const closing = match[0].startsWith('</');
    const selfClosing = match[0].endsWith('/>');
    if (closing) {
      if (tags.pop() !== name) reasons.push('unbalanced');
    } else if (!selfClosing) tags.push(name);
  }
  const tail = value.slice(last);
  if (tail.includes('<') || tail.includes('>')) reasons.push('raw-bracket');
  if (tags.length) reasons.push('unbalanced');
  if (/&(?!(?:amp|lt|gt|quot|#\d+|#x[0-9a-fA-F]+);)/.test(value)) reasons.push('bad-entity');
  return { ok: reasons.length === 0, reasons: [...new Set(reasons)] };
}

function openTagsIn(str) {
  const stack = [];
  const re = /<\/?([a-zA-Z0-9-]+)(?:\s[^<>]*)?>/g;
  let match;
  while ((match = re.exec(str))) {
    const name = match[1].toLowerCase();
    if (!ALLOWED_TAGS.has(name)) continue;
    const closing = match[0].startsWith('</');
    const selfClosing = match[0].endsWith('/>');
    if (closing) {
      const idx = stack.lastIndexOf(name);
      if (idx !== -1) stack.splice(idx, 1);
    } else if (!selfClosing) {
      stack.push(name);
    }
  }
  return stack;
}

function fitTelegram(text) {
  const value = String(text || '');
  if (value.length <= 4096) return value;
  const note = '\n…';
  let targetLen = 4096 - note.length;
  for (let iter = 0; iter < 10; iter++) {
    if (targetLen <= 0) break;
    let cut = value.slice(0, targetLen);
    if (/[\uD800-\uDBFF]$/.test(cut)) cut = cut.slice(0, -1);
    const lastOpenTag = cut.lastIndexOf('<');
    const lastCloseTag = cut.lastIndexOf('>');
    if (lastOpenTag > lastCloseTag) cut = cut.slice(0, lastOpenTag);
    const lastAmp = cut.lastIndexOf('&');
    const lastSemi = cut.lastIndexOf(';');
    if (lastAmp > lastSemi && cut.slice(lastAmp).length < 10) cut = cut.slice(0, lastAmp);
    const line = cut.lastIndexOf('\n');
    let kept = (line > targetLen - 600 && line > 0) ? cut.slice(0, line) : cut;
    if (/[\uD800-\uDBFF]$/.test(kept)) kept = kept.slice(0, -1);
    const open = openTagsIn(kept);
    const closing = open.reverse().map(t => '</' + t + '>').join('');
    if (kept.length + closing.length + note.length <= 4096) {
      return kept + closing + note;
    }
    targetLen = 4096 - note.length - closing.length - 10;
  }
  let fallback = value.slice(0, 4096 - note.length);
  if (/[\uD800-\uDBFF]$/.test(fallback)) fallback = fallback.slice(0, -1);
  return fallback + note;
}

module.exports = {
  SUPPORTED_LANGUAGES,
  CATALOG,
  normalizeLanguage,
  languageOrDefault,
  languageName,
  say,
  telegramAlertLabel,
  plainAlert,
  stateLabel,
  messageKeys,
  isFragment,
  commandVerb,
  isDetailsRequest,
  parseLanguageCommand,
  reasonLine,
  companionOffline,
  telegramReadable,
  fitTelegram
};
