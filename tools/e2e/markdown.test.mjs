// Unit tests for the write-up editor's Markdown model (PhotoCurator.Core/Assets/markdown.js).
// Run: node --test tools/e2e/markdown.test.mjs

import assert from 'node:assert/strict';
import test from 'node:test';
import { parseBlocks, serializeBlocks, inlineToHtml, escapeText, photoMarkdown } from '../../PhotoCurator.Core/Assets/markdown.js';

const shape = blocks => blocks.map(({ id, ...rest }) => rest);

test('splits a story into blocks', () => {
  const md = [
    '# Attic insulation',
    'The attic had **40 years** of old insulation.',
    'It was a mess.',
    '',
    '![Pulling it out](IMG_0172.HEIC)',
    '![Suited up](<Big day/IMG 1.jpg> "t")',
    '',
    '- masks',
    '- suits',
    '',
    '1. out',
    '2. in',
    '',
    '> Worth it.',
    '',
    '```',
    'code',
    '',
    'more code',
    '```'
  ].join('\n');

  assert.deepEqual(shape(parseBlocks(md)), [
    { type: 'heading', level: 1, md: 'Attic insulation' },
    { type: 'paragraph', md: 'The attic had **40 years** of old insulation.\nIt was a mess.' },
    { type: 'photo', caption: 'Pulling it out', path: 'IMG_0172.HEIC', title: null },
    { type: 'photo', caption: 'Suited up', path: 'Big day/IMG 1.jpg', title: 't' },
    { type: 'list', ordered: false, items: ['masks', 'suits'] },
    { type: 'list', ordered: true, items: ['out', 'in'] },
    { type: 'quote', md: 'Worth it.' },
    { type: 'raw', md: '```\ncode\n\nmore code\n```' }
  ]);
});

test('keeps what it does not model exactly', () => {
  for (const md of ['| a | b |\n|---|---|\n| 1 | 2 |', '<div>hi</div>', 'Text ![inline](a.jpg) more', '- a\n  - nested', 'Title\n===']) {
    const blocks = parseBlocks(md);
    assert.equal(blocks.length, 1, md);
    assert.equal(blocks[0].type, 'raw', md);
    assert.equal(serializeBlocks(blocks), md);
  }
});

test('round-trips canonical markdown', () => {
  const md = '## Before\n\nOne line.  \nHard break above.\n\n![A \\[b\\]](IMG_1.jpg)\n\n- one\n- two\n  continued\n\n> quote\n> two lines';
  assert.equal(serializeBlocks(parseBlocks(md)), md);
});

test('empty text blocks are left out, photos are not', () => {
  const blocks = parseBlocks('Hello');
  blocks.push({ id: 99, type: 'paragraph', md: '' }, { id: 100, type: 'heading', level: 2, md: '' }, { id: 101, type: 'photo', caption: '', path: 'x.jpg' });
  assert.equal(serializeBlocks(blocks), 'Hello\n\n![](x.jpg)');
});

test('a paragraph that starts like another block is escaped', () => {
  for (const text of ['# not a heading', '- not a list', '1. not a list', '> not a quote']) {
    const md = serializeBlocks([{ id: 1, type: 'paragraph', md: text }]);
    const back = parseBlocks(md);
    assert.equal(back.length, 1);
    assert.equal(back[0].type, 'paragraph', text);
  }
});

test('inline markdown to html', () => {
  assert.equal(inlineToHtml('a **b** *c* _d_ `e*f` [g](https://x.y)'), 'a <strong>b</strong> <em>c</em> <em>d</em> <code>e*f</code> <a href="https://x.y">g</a>');
  assert.equal(inlineToHtml('snake_case_name and 2*3*4'), 'snake_case_name and 2<em>3</em>4');
  assert.equal(inlineToHtml('\\*literal\\* <b>&'), '*literal* &lt;b&gt;&amp;');
  assert.equal(inlineToHtml('one  \ntwo\nthree'), 'one<br>two<br data-soft="">three');
  assert.equal(inlineToHtml('**bold *and italic***'), '<strong>bold <em>and italic</em></strong>');
});

test('typed text is escaped so it reads back the same', () => {
  const typed = 'Use *stars*, [brackets], <tags>, back\\slash and snake_case or _this_';
  const md = escapeText(typed);
  assert.equal(md, 'Use \\*stars\\*, \\[brackets\\], \\<tags>, back\\\\slash and snake_case or \\_this\\_');
  assert.equal(inlineToHtml(md), 'Use *stars*, [brackets], &lt;tags&gt;, back\\slash and snake_case or _this_');
});

test('photo markdown', () => {
  assert.equal(photoMarkdown('Caption', 'IMG_1.jpg'), '![Caption](IMG_1.jpg)');
  assert.equal(photoMarkdown('A [b]', 'Big Sur/IMG 1.jpg'), '![A \\[b\\]](<Big Sur/IMG 1.jpg>)');
  assert.equal(photoMarkdown('two\nlines', 'x(1).jpg', 'T'), '![two lines](<x(1).jpg> "T")');
  const [back] = parseBlocks(photoMarkdown('A [b]', 'Big Sur/IMG 1.jpg'));
  assert.equal(back.caption, 'A [b]');
  assert.equal(back.path, 'Big Sur/IMG 1.jpg');
});
