import * as vscode from 'vscode'; //Imports
import * as cp from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import * as responses from './responses';
import { pickRandom, getCurrentSkin, ensureNotesInGitignore } from './util'
import { startSlime, stopSlime } from './processManager';
import { eventNames } from 'process';
import { spawn } from 'child_process';

const STATUS_FILE = path.join(os.tmpdir(), 'slime_status.txt'); //hard setting

let workStartTime = Date.now(); //hard coded times 
let breakIntervalTime = 1000 * 60 * 55;
let lastActiveTime = Date.now();
const afkThreshold = 1000 * 60 * 6;

//spam filter
let lastCommentTime = 0;

let streakMinutes = 0; //streaks
const streakThreshold = 5;

let gitState = { status: 'OK', message: '' }; //slime states
let diagState = { status: 'OK', message: '' };
let breakState = { status: 'OK', message: '' };
let keywordState = { status: 'OK', message: '' };

let lastText = "";
let userHasTyped = false;

let keywordTimer: NodeJS.Timeout | undefined; //mixed
let lastStatus = '';
const SETTINGS_FILE = path.join(os.tmpdir(), 'slime_settings.json');

let pasteCount = 0; // anti-vibe coding (this is funny)
let lastPasteTime = Date.now();
const PASTE_RESET_TIME = 1000 * 60 * 4;
const PASTE_LIMIT = 4;

//git naging fix
let lastDirtyNagTime = 0;
const DIRTY_NAG_COOLDOWN = 1000 * 60 * 30;
//git commited
let lastGitWasDirty = false;


//main software
export function activate(context: vscode.ExtensionContext) {


	console.log('--- START: Extension "vsc-helper" is now running! ---');

	startSlime(context);

	const COMMAND_FILE = path.join(os.tmpdir(), 'slime_command.txt');
	console.log("VS Code is watching this path: " + COMMAND_FILE);

	const watcher = vscode.workspace.createFileSystemWatcher(COMMAND_FILE);
	if (!fs.existsSync(COMMAND_FILE)) {
		fs.writeFileSync(COMMAND_FILE, '');
	}

	fs.watchFile(SETTINGS_FILE, { interval: 500 }, (curr, prev) => {
		if (curr.mtime !== prev.mtime) {
			console.log("Skin changed in C# app! Refreshing VS Code logic...");
			refreshSlime();
		}
	});

	context.subscriptions.push({ dispose: () => fs.unwatchFile(SETTINGS_FILE) });

	// Använd fs.watchFile för filer utanför workspace (säkrare för Temp)
	fs.watchFile(COMMAND_FILE, { interval: 500 }, (curr, prev) => {
		if (curr.mtime !== prev.mtime) {
			try {
				const command = fs.readFileSync(COMMAND_FILE, 'utf8').trim();
				if (command === 'OPEN_NOTES') {
					console.log("Mottog OPEN_NOTES via watchFile!");
					vscode.commands.executeCommand('slime.openNotes');
					fs.writeFileSync(COMMAND_FILE, ''); // Rensa filen
				}
			} catch (err) {
				console.error("Error while reading slime_command.txt:", err);
			}
		}
	});

	context.subscriptions.push({ dispose: () => fs.unwatchFile(COMMAND_FILE) });

	updateSlime('IDLE', 'Slime is waking up...');

	vscode.workspace.onDidChangeTextDocument(() => {
		lastActiveTime = Date.now();
	});

	vscode.window.onDidChangeTextEditorSelection(() => {
		lastActiveTime = Date.now();
	})

	//intervals

	const fastInterval = setInterval(() => {
		checkDiagnostics(); //error and warning
	}, 1000 * 5);

	const slowInterval = setInterval(() => {
		checkSlimeNotes(context); //check notes
	}, 1000 * 60 * 5); //1000 * 60 * 5

	const gitInterval = setInterval(() => {
		checkGitStatus(); //check git
	}, 1000 * 60 * 45);

	const breakInterval = setInterval(() => {
		checkBreakTime();
		updateStreak();
	}, 1000 * 60);

	checkGitStatus();

	const gitWatcher = vscode.workspace.createFileSystemWatcher('**/.git/index');

	gitWatcher.onDidChange(() => {
		console.log("Git change detected! Checking status...");
		setTimeout(() => checkGitStatus(), 1000);  // Kör checken omedelbart
	});

	gitWatcher.onDidCreate(() => checkGitStatus());

	context.subscriptions.push(gitWatcher);

	context.subscriptions.push({ dispose: () => clearInterval(fastInterval) });
	context.subscriptions.push({ dispose: () => clearInterval(slowInterval) });
	context.subscriptions.push({ dispose: () => clearInterval(breakInterval) });



	const handleActivity = () => {
		lastActiveTime = Date.now();
		userHasTyped = true;

		if (lastStatus === 'AFK') {
			refreshSlime();
		}
	};
	//File creation and delete reaction
	const onCreate = vscode.workspace.onDidCreateFiles((event) => {
		const fileName = event.files[0].fsPath.toLowerCase();

		if (fileName.includes('test')) {
			triggerReaction('STREAK', 'Writing tests? I love testing new things')
		}
		else {
			triggerReaction('DIRTY', 'Making a new file are we?!');
		}
		setTimeout(() => checkGitStatus(), 2000);
	});

	const onDelte = vscode.workspace.onDidDeleteFiles((event) => {
		triggerReaction('ANNOYED', 'Well... we didnt need that anyway?');
		setTimeout(() => checkGitStatus(), 2000);
	});

	context.subscriptions.push(onCreate, onDelte);

	//anti vibe coding section
	vscode.workspace.onDidChangeTextDocument((event) => {
		handleActivity();
		lastActiveTime = Date.now();
		userHasTyped = true;
		const changes = event.contentChanges[0];
		if (!changes) return;

		if (changes.text.length > 50) {
			const now = Date.now();

			if (now - lastPasteTime > PASTE_RESET_TIME) {
				pasteCount = 0;
			}

			pasteCount++;
			lastPasteTime = now;

			if (pasteCount >= PASTE_LIMIT) {
				triggerReaction('ANNOYED', pickRandom(responses.copyPasteResponses));

				pasteCount = 0;
			}
		}

		vscode.window.onDidChangeTextEditorSelection(() => {
			handleActivity();
		});

		const currentLine = event.document.lineAt(changes.range.start.line).text.toLocaleLowerCase();

		//random phrases 

		if (currentLine.includes('console.log') || currentLine.includes('print(') || currentLine.includes('writeline')) {
			triggerReaction('BREAK', pickRandom(responses.consoleResponses));
		}
		else if (currentLine.includes('debug')) {
			triggerReaction('POKE', pickRandom(responses.debugResponses));
		}
		else if (currentLine.includes('1337') || currentLine.includes('hacker') || currentLine.includes('root')) {
			triggerReaction('STREAK', pickRandom(responses.coolResponses));
		}
		else if (currentLine.includes('fuck') || currentLine.includes('damn') || currentLine.includes('fucking') || currentLine.includes('shit')) {
			triggerReaction('ANNOYED', pickRandom(responses.swearResponses));
		}
		else if (currentLine.includes('//') || currentLine.includes('/*') || currentLine.includes('<!--')) {
			const now = Date.now();
			if (now - lastCommentTime > 8000) {
				triggerReaction('FUNNY', pickRandom(responses.commentResponses));
				lastCommentTime = now;
			}
		}
		else if (currentLine.includes('slime')) {
			triggerReaction('STREAK', 'We\'re talking about me?');
		}
		else if (currentLine.includes('todo') || currentLine.includes('fixme')) {
			triggerReaction('TIRED', pickRandom(responses.todoResponses));
		}
		else if (currentLine.includes('foo') || currentLine.includes('bar') || currentLine.includes('temp')) {
			triggerReaction('FUNNY', pickRandom(responses.funnyResponses));
		}
		else if (currentLine.includes('while(true)') || currentLine.includes('for(;;)')) {
			triggerReaction('ERROR', 'Wait... Eternity loop?!?!?!');
		}
		else if (currentLine.includes('return null') || currentLine.includes('null;')) {
			triggerReaction('PUSH_NEEDED', 'Null? Are you sure about that?');
		}
		else if (currentLine.includes('!important')) {
			triggerReaction('ANNOYED', 'Cheater! Dont use !important!');
		}
		else if (currentLine.includes('http') || currentLine.includes('www.') || currentLine.includes('stackoverflow')) {
			triggerReaction('FUNNY', 'Ctrl+C, Ctrl+V champion!');
		}
		else if (currentLine.includes('await ') || currentLine.includes('async ') || currentLine.includes('thread.sleep')) {
			triggerReaction('AFK', 'Zzz... Yeah I\'m waiting...');
		}
		else if (currentLine.includes('coffee') || currentLine.includes('drink') || currentLine.includes('pizza') || currentLine.includes('snack')) {
			triggerReaction('BREAK', 'Did you say food? I want some! Give me!');
		}
		else if (currentLine.includes('sudo ')) {
			triggerReaction('STREAK', 'Yes master!');
		}
		else if (currentLine.includes(': any') || currentLine.includes('as any')) {
			triggerReaction('FUNNY', 'Type safety? Never heard of it?');
		}
		else if (currentLine.includes('try {') || currentLine.includes('catch (')) {
			triggerReaction('POKE', 'Preparing for disaster?');
		}
		else if (currentLine.includes('localhost') || currentLine.includes('127.0.0.1')) {
			triggerReaction('FUNNY', 'I found the connection!');
		}
		else if (currentLine.includes('drop table') || currentLine.includes('delete from') || currentLine.includes('truncate')) {
			triggerReaction('POKE', 'Wait! Don\'t delete the database!');
		}
		else if (currentLine.includes('select *')) {
			triggerReaction('STREAK', 'Give me ALL the data!');
		}
		else if (currentLine.includes('git push --force') || currentLine.includes('git commit -m "fix"')) {
			triggerReaction('STREAK', 'Living dangerously I see!');
		}
		else if (currentLine.includes('<<<<<<< HEAD')) {
			triggerReaction('ERROR', 'Merge conflict! Fight!');
		}
		else if (currentLine.includes('regex') || currentLine.includes('regexp') || currentLine.includes('^.*$')) {
			triggerReaction('WARNING', 'Magic spells? I don\'t speak RegEx');
		}
		else if (currentLine.includes('copilot') || currentLine.includes('chatgpt') || currentLine.includes('generate')) {
			triggerReaction('STREAK', 'Am I being replaced by another AI?');
		}
		else if (currentLine.includes('border:') && currentLine.includes('red')) {
			triggerReaction('FUNNY', 'CSS Debugging? Classic moves.');
		}
		else if (currentLine.includes('password =') || currentLine.includes('secret =')) {
			triggerReaction('WARNING', 'Don\'t hardcode secrets!');
		}

	});

	//45 sec slime thoughts pattern

	let idleTalkTimer: NodeJS.Timeout | undefined;

	vscode.workspace.onDidChangeTextDocument(() => {
		if (idleTalkTimer) clearTimeout(idleTalkTimer);

		idleTalkTimer = setTimeout(() => {
			if (diagState.status === 'OK' && breakState.status === 'OK') {
				const thought = pickRandom(responses.idleThoughts);
				updateSlime('IDLE', thought);
				streakMinutes = 0;
			}
		}, 1000 * 45);
	});

	// slime notes
	let openNotescmd = vscode.commands.registerCommand('slime.openNotes', async () => {

		const workspaceFolders = vscode.workspace.workspaceFolders;

		if (!workspaceFolders || workspaceFolders.length === 0) {
			vscode.window.showErrorMessage("Open a folder first to use Slime-Notes");
			return;
		}

		const rootPath = workspaceFolders[0].uri.fsPath;
		const notesUri = vscode.Uri.file(path.join(rootPath, 'slime_notes.md'));

		if (!fs.existsSync(notesUri.fsPath)) {
			const initialContent = "# Slime Notes & TODOs\n\n- [ ] My first To-Do";
			fs.writeFileSync(notesUri.fsPath, initialContent, 'utf8');

			ensureNotesInGitignore(rootPath);
		}

		try {
			const doc = await vscode.workspace.openTextDocument(notesUri);
			await vscode.window.showTextDocument(doc);
		} catch (error) {
			vscode.window.showErrorMessage("Could not open Slime-Notes");
		}
	});

	context.subscriptions.push(openNotescmd);

	let askCommand = vscode.commands.registerCommand('slime.ask', async () => {
		const userInput = await vscode.window.showInputBox({
			prompt: "What do you want to ask your Slime?",
			placeHolder: "e.g. Why is my code not working?"
		});

		if (userInput) {
			// Vi anropar funktionen som ligger längre ner i filen
			askSlime(userInput, context);
		}
	});

	context.subscriptions.push(askCommand);
}
//end of activate

// main brain
function refreshSlime() {

	const timeScenceActive = Date.now() - lastActiveTime;
	const isAfk = timeScenceActive > afkThreshold;

	let finalStatus = 'IDLE';
	let finalMessage = '';

	if (breakState.status === 'BREAK') {
		finalStatus = 'BREAK';
		finalMessage = breakState.message;
	}
	else if (keywordState.status !== 'OK') {
		finalStatus = keywordState.status;
		finalMessage = keywordState.message;
	}
	else if (isAfk) {
		finalStatus = 'AFK';
		finalMessage = 'Zzz...'
	}
	else if (diagState.status === 'ERROR') {
		finalStatus = 'ERROR';
		finalMessage = diagState.message;
	}
	else if (diagState.status === 'WARNING') {
		finalStatus = 'WARNING';
		finalMessage = diagState.message;
	}
	else if (streakMinutes >= streakThreshold) {
		finalStatus = 'STREAK';
		finalMessage = `You're on fire! ${streakMinutes} min`;
	}
	else if (gitState.status === 'DIRTY' && (Date.now() - lastDirtyNagTime < 15000)) {
		finalStatus = 'DIRTY';
		finalMessage = gitState.message;
	}
	else if (gitState.status === 'PUSH_NEEDED') {
		finalStatus = 'PUSH_NEEDED';
		finalMessage = gitState.message;
	}
	else {
		finalStatus = 'IDLE';
		finalMessage = '';
	}

	updateSlime(finalStatus, finalMessage);
}
//check errors and warnings
function checkDiagnostics() {
	let errorCount = 0;
	let warningCount = 0;
	let semicolonLine = -1;

	const skin = getCurrentSkin() as keyof typeof responses.skinPhrases;
	const phrases = responses.skinPhrases[skin] || responses.skinPhrases.Default;

	const allDiagnostics = vscode.languages.getDiagnostics();

	for (const [uri, diagnostics] of allDiagnostics) {
		if (uri.fsPath.includes('node_modules')) continue;

		diagnostics.forEach(diag => {
			if (diag.severity === vscode.DiagnosticSeverity.Error) {
				errorCount++;
				if (diag.message.includes("';'") || diag.message.includes("expected ;")) {
					semicolonLine = diag.range.start.line + 1;
				}
			}

			else if (diag.severity === vscode.DiagnosticSeverity.Warning) {
				warningCount++;
			}
		});
	}
	//error messages
	let status: string = 'OK';
	let message: string = '';

	if (semicolonLine !== -1) {
		status = 'ERROR';
		message = phrases.semicolon.replace('{line}', semicolonLine.toString());
	}
	else if (errorCount > 0) {
		status = 'ERROR';
		message = phrases.error.replace('{n}', errorCount.toString());

		if (errorCount > 1) streakMinutes = 0;
	}
	else if (warningCount > 0) {
		status = 'WARNING';
		message = phrases.warning.replace('{n}', warningCount.toString());
	}

	diagState = { status, message };

	refreshSlime();
}
//check git
function checkGitStatus() {
	const workspaceFolders = vscode.workspace.workspaceFolders;
	if (!workspaceFolders) return;
	const rootPath = workspaceFolders[0].uri.fsPath;

	cp.exec('git status --porcelain', { cwd: rootPath }, (err, stdout) => {
		if (err) { return; }

		const isDirty = stdout.trim().length > 0;
		const now = Date.now();

		if (isDirty) {
			gitState = { status: 'DIRTY', message: 'You have uncommitted changes!' };

			if (!lastGitWasDirty || (now - lastDirtyNagTime > DIRTY_NAG_COOLDOWN)) {
				triggerReaction('DIRTY', 'You have forgotten to commit your code!');
				lastDirtyNagTime = now;
			}
			lastGitWasDirty = true;
		}
		else {
			if (lastGitWasDirty) {
				triggerReaction('STREAK', 'Great commit! Now the code is safe and sound');
				lastDirtyNagTime = 0;
			}
			gitState = { status: 'OK', message: '' };
			lastGitWasDirty = false;
			if (keywordState.status === 'DIRTY') {
				keywordState = { status: 'OK', message: '' };
				if (keywordTimer) clearTimeout(keywordTimer);
			}
			checkIfNeedToPush(rootPath);
		}
		refreshSlime();
	});
}
//check git
function checkIfNeedToPush(cwd: string) {
	cp.exec('git log @{u}..', { cwd }, (err, stdout) => {

		if (err) { return; }

		const lines = stdout.trim().split('\n').filter(l => l.length > 0);
		const commitCount = lines.length;

		if (commitCount > 0) {
			let msg = 'Maybe it is time to push the code?';

			if (commitCount > 10) {
				msg = `Wow, ${commitCount} local commits? Push already!`;
			}

			gitState = { status: 'PUSH_NEEDED', message: msg };
		} else {
			gitState = { status: 'OK', message: '' }
		}
		refreshSlime();
	});
}
//basic update
function updateSlime(status: string, message: string = "") {

	if (status === lastStatus && message === lastText) {
		return;
	}

	try {
		const data = { status: status, text: message };
		fs.writeFileSync(STATUS_FILE, JSON.stringify(data), 'utf8');

		if (status !== lastStatus) {
			console.log(`SLIME STATUS: ${status}`);
			lastStatus = status;
		}
		lastText = message;

	} catch (error) {
		console.error('Could not write to file:', error);
	}
}
//take a break reminder
function checkBreakTime() {

	if (breakState.status === 'BREAK') { return; }

	const timeWorked = Date.now() - workStartTime;

	if (timeWorked > breakIntervalTime) {

		const randomMessage = pickRandom(responses.breakResponses)

		breakState = { status: 'BREAK', message: randomMessage };
		refreshSlime();

		setTimeout(() => {
			workStartTime = Date.now();
			breakState = { status: 'OK', message: '' };
			refreshSlime();
		}, 1000 * 30);
	}
}
//streak funtion. based on how long you have been writing code.
function updateStreak() {

	const timeScenceActive = Date.now() - lastActiveTime;
	const isAfk = timeScenceActive > afkThreshold;

	if (diagState.status === 'OK' && breakState.status === 'OK' && !isAfk && userHasTyped) {
		streakMinutes++;
	} else {
		streakMinutes = 0;
	}
	userHasTyped = false;
	refreshSlime();
}
//part of the streak funtion
function triggerReaction(status: string, message: string) {
	keywordState = { status: status, message: message };

	refreshSlime();

	if (keywordTimer) clearTimeout(keywordTimer);

	keywordTimer = setTimeout(() => {
		keywordState = { status: 'OK', message: '' };
		refreshSlime();
	}, 10000);
}


//slime notes
function checkSlimeNotes(context: vscode.ExtensionContext) {
	const workspaceFolders = vscode.workspace.workspaceFolders;
	if (!workspaceFolders || workspaceFolders.length === 0) return;

	const notesPath = path.join(workspaceFolders[0].uri.fsPath, 'slime_notes.md');

	if (fs.existsSync(notesPath)) {
		const content = fs.readFileSync(notesPath, 'utf8');
		const todoCount = (content.match(/- \[ \]/g) || []).length;

		if (lastStatus == 'IDLE' || lastStatus == 'AFK') {

			if (todoCount > 0 && keywordState.status === 'OK') {
				updateSlime('IDLE', `Don't forget, we have ${todoCount} To-do's`);
			}
			if (content.length > 10) {
				askSlime(`Here are my notes: ${content.substring(0, 500)}. Give me a very short, sassy reminder of what I should be doing.`, context);
			}
		}
	}
}

function askSlime(query: string, context: vscode.ExtensionContext) {
	// Nu använder vi context.extensionPath för att hitta rätt mapp
	const cliPath = path.join(context.extensionPath, 'out', 'slime-cli.js');

	cp.exec(`node "${cliPath}" "${query}"`, (err) => {
		if (err) {
			console.error("Slime CLI Error:", err);
		}
	});
}

//when quiting
export function deactivate() {
	console.log('Slime is going to sleep');
	stopSlime();
}

