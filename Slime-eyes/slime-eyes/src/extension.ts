import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';

let statusDebounceTimer: NodeJS.Timeout | undefined;
const statusFilePath = path.join(os.tmpdir(), 'slime_status.txt');

export function activate(context: vscode.ExtensionContext) {
	console.log('Slime Eyes are now active!');

	// Lyssna på diagnostik (fel, varningar och syntaxproblem) i realtid
	const diagListener = vscode.languages.onDidChangeDiagnostics(() => {
		scheduleStatusUpdate();
	});

	// Lyssna när användaren byter aktiv flik/fil
	const tabListener = vscode.window.onDidChangeActiveTextEditor(() => {
		scheduleStatusUpdate();
	});

	context.subscriptions.push(diagListener, tabListener);

	// Kör en första scanning direkt vid start
	scheduleStatusUpdate();
}

function scheduleStatusUpdate() {
	// Debounce på 300 ms så vi inte spammar filsystemet vid snabb inmatning
	if (statusDebounceTimer) {
		clearTimeout(statusDebounceTimer);
	}
	statusDebounceTimer = setTimeout(updateSlimeStatus, 300);
}

function updateSlimeStatus() {
	const editor = vscode.window.activeTextEditor;
	if (!editor) {
		return;
	}

	// Hämta alla diagnostikmeddelanden för den aktiva filen/notebooken
	const currentUri = editor.document.uri;
	const diagnostics = vscode.languages.getDiagnostics(currentUri);

	let errorCount = 0;
	let warningCount = 0;
	let firstErrorMessage = '';
	let missingSemicolonLine: number | null = null;

	for (const diag of diagnostics) {
		if (diag.severity === vscode.DiagnosticSeverity.Error) {
			errorCount++;
			if (!firstErrorMessage) {
				firstErrorMessage = diag.message;
			}

			// Upptäck vanliga "missing semicolon"-fel
			const msg = diag.message.toLowerCase();
			if (msg.includes(';') || msg.includes('semicolon') || msg.includes('expected ;')) {
				missingSemicolonLine = diag.range.start.line + 1;
			}
		} else if (diag.severity === vscode.DiagnosticSeverity.Warning) {
			warningCount++;
		}
	}

	let status = "IDLE";
	let text = "";

	if (errorCount > 0) {
		status = "ERROR";
		if (missingSemicolonLine !== null) {
			text = `Missing ; on line ${missingSemicolonLine}!`;
		} else if (errorCount === 1) {
			text = `Bug detected: ${firstErrorMessage}`;
		} else {
			text = `You have ${errorCount} errors in your code!`;
		}
	} else if (warningCount > 0) {
		status = "WARNING";
		text = `Careful! You have ${warningCount} warning(s)...`;
	}

	// Skriv status till slime_status.txt i JSON-format för C#-appen
	const payload = JSON.stringify({
		status: status,
		text: text
	});

	try {
		fs.writeFileSync(statusFilePath, payload, 'utf8');
	} catch (err) {
		console.error('Could not write to slime_status.txt', err);
	}
}

export function deactivate() {
	// Återställ till IDLE när VS Code stängs
	try {
		const payload = JSON.stringify({ status: "IDLE", text: "" });
		fs.writeFileSync(statusFilePath, payload, 'utf8');
	} catch { }
}