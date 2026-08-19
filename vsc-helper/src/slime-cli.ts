#!/usr/bin/env node

import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';


const COMMAND_FILE = path.join(os.tmpdir(), 'slime_command.txt');

async function run() {
  const args = process.argv.slice(2);
  const userQuery = args.join(' ');

  if (!userQuery) {
    console.log("Usage: slime <din fråga>");
    return;
  }

  const command = `ASK_AI:${userQuery}`;

  try {
    fs.writeFileSync(COMMAND_FILE, command, 'utf8');
    console.log(`✨ Slime is thinking about: "${userQuery}"`);
  } catch (err) {
    console.error("Could not reach Slime Helper:", err);
  }
}

run();