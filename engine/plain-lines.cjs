'use strict';

// Plain words for the native screens (job 31): one short line per step and a short summary of a
// finished work, written by the lead. When the lead gives none, a safe short text is made from the
// step title or the first sentence of what is recorded. Nothing here calls a model.

const LINE_MAX = 90;
const SUMMARY_MAX = 280;
const VERIFY_PREFIX = 'Verify the work independently';

// Asked of the lead with the split prompt and the review prompt; the answers stay optional.
const SPLIT_LINE_ASK = 'For each part also give "line": one short plain sentence for the owner, at most 12 words, that says who does what (for example "Opus builds the weather page"). No file paths, no code.';
const SUMMARY_ASK = 'Also give "summary": one or two plain sentences for the owner that say what the work did, from the results above, at most 280 characters. No file paths, no code, no test names.';

// One line of plain text: no markdown marks, no line breaks, no repeated spaces.
function flat(text) {
  if (typeof text !== 'string') return '';
  return text.replace(/```[\s\S]*?```/g, ' ').replace(/`([^`]*)`/g, '$1').replace(/\*\*|__/g, '').replace(/^\s*#+\s*/gm, '')
    .replace(/^\s*[-*•]\s+/gm, '').replace(/\s+/g, ' ').trim();
}

// The text cut at a word, with an ellipsis, so it never passes max characters.
function clip(text, max) {
  if (text.length <= max) return text;
  const cut = text.slice(0, max - 1);
  const space = cut.lastIndexOf(' ');
  return (space > max / 2 ? cut.slice(0, space) : cut).replace(/[\s,;:.\-–]+$/, '') + '…';
}

// The first sentence: up to the first ". ", "! ", "? " or ": " (a colon starts the instructions).
function firstSentence(text) {
  const plain = flat(text);
  const end = plain.search(/[.!?](\s|$)|:\s/);
  return (end >= 0 ? plain.slice(0, plain[end] === ':' ? end : end + 1) : plain).trim();
}

// Up to the first n sentences.
function sentences(text, count) {
  const plain = flat(text);
  const parts = plain.match(/[^.!?]+[.!?]+(\s|$)|[^.!?]+$/g) || [];
  return parts.slice(0, count).join('').trim();
}

function shortLine(text, max = LINE_MAX) { return clip(flat(text), max); }

// A list from the store: missing or the wrong type is empty, so a corrupt snapshot still reads.
function list(value) { return Array.isArray(value) ? value : []; }

// The line shown for a step: the lead's line, or else the first sentence of its title.
function stepLine(sub) {
  if (!sub || typeof sub !== 'object') return { line: '', from: 'none' };
  const lead = shortLine(sub.leadLine || sub.line);
  if (lead) return { line: lead, from: 'lead' };
  const title = typeof sub.title === 'string' ? sub.title : '';
  if (title.startsWith(VERIFY_PREFIX)) return { line: 'Checks the whole work and runs the tests.', from: 'fallback' };
  const fallback = clip(firstSentence(title), LINE_MAX);
  return { line: fallback, from: fallback ? 'fallback' : 'none' };
}

// What the work did, in one or two sentences: the lead's summary, or else the first sentence of the
// last finished build step's result, or else the work's title.
function workSummary(mega, results = [], subTasks = []) {
  const lead = clip(sentences(mega && mega.leadSummary, 2), SUMMARY_MAX);
  if (lead) return { summary: lead, from: 'lead' };
  const verify = new Set(list(subTasks).filter(sub => sub && typeof sub.title === 'string' && sub.title.startsWith(VERIFY_PREFIX)).map(sub => sub.id));
  const built = list(results).filter(result => result && result.status === 'COMPLETE' && !verify.has(result.subTaskId) && flat(result.summary));
  const said = built.length ? firstSentence(built.at(-1).summary) : '';
  if (said) return { summary: clip(said, SUMMARY_MAX), from: 'fallback' };
  const title = clip(firstSentence(mega && (mega.title || mega.goal)), SUMMARY_MAX);
  return { summary: title, from: title ? 'fallback' : 'none' };
}

// Every balanced {...} block that is JSON, as autonomy.cjs reads the lead's replies.
function jsonObjects(text) {
  const found = [];
  if (typeof text !== 'string') return found;
  for (let start = text.indexOf('{'); start >= 0; start = text.indexOf('{', start + 1)) {
    let depth = 0, quoted = false, escaped = false;
    for (let index = start; index < text.length; index += 1) {
      const char = text[index];
      if (quoted) { if (escaped) escaped = false; else if (char === '\\') escaped = true; else if (char === '"') quoted = false; continue; }
      if (char === '"') quoted = true;
      else if (char === '{') depth += 1;
      else if (char === '}' && --depth === 0) { try { found.push(JSON.parse(text.slice(start, index + 1))); } catch { /* not JSON */ } break; }
    }
  }
  return found;
}

// The "line" of each part in the lead's split reply, by index; '' where the lead wrote none.
function readSplitLines(text) {
  const parsed = jsonObjects(text).filter(item => item && Array.isArray(item.parts)).at(-1);
  if (!parsed) return [];
  return parsed.parts.map(part => shortLine(part && typeof part.line === 'string' ? part.line : ''));
}

// The "summary" in the lead's review reply; '' when there is none.
function readSummary(text) {
  const parsed = jsonObjects(text).filter(item => item && typeof item.done === 'boolean').at(-1);
  return parsed && typeof parsed.summary === 'string' ? clip(sentences(parsed.summary, 2), SUMMARY_MAX) : '';
}

// What the native app reads with a work row: the summary and one line per step, each with its source.
function forWork(mega, subTasks = [], results = []) {
  const { summary, from } = workSummary(mega, results, subTasks);
  return { summary, summaryFrom: from, steps: list(subTasks).filter(Boolean).map(sub => ({ id: sub.id, ...stepLine(sub) })) };
}

module.exports = { LINE_MAX, SUMMARY_MAX, SPLIT_LINE_ASK, SUMMARY_ASK, flat, clip, sentences, firstSentence, shortLine, stepLine, workSummary, readSplitLines, readSummary, forWork };
