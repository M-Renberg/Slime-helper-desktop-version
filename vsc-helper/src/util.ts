import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';


//random phrases

export function pickRandom(arr: string[]) {
  return arr[Math.floor(Math.random() * arr.length)];
}

// //get skin from json so we can use it.

export function getCurrentSkin(): string {
  const settingsPath = path.join(os.tmpdir(), 'slime_settings.json');
  if (fs.existsSync(settingsPath)) {
    const settings = JSON.parse(fs.readFileSync(settingsPath, 'utf8'));
    return settings.CurrentSkin || "Default";
  }
  return "Default";
}

//add gitingore when creating slime notes
export async function ensureNotesInGitignore(workspaceRoot: string) {
  const gitignorePath = path.join(workspaceRoot, '.gitignore');
  const notesFileName = 'slime_notes.md';

  if (fs.existsSync(gitignorePath)) {
    let content = fs.readFileSync(gitignorePath, 'utf8');

    const regex = new RegExp(`^${notesFileName.replace('.', '\\.')}\\s*$`, 'm');

    if (!regex.test(content)) {
      const newContent = content.endsWith('\n')
        ? `${content}\n# Slime Helper Notes\n${notesFileName}\n`
        : `${content}\n\n# Slime Helper Notes\n${notesFileName}\n`;

      fs.writeFileSync(gitignorePath, newContent, 'utf8');
      console.log(`${notesFileName} tillagd i .gitignore`);
    }
  }
}