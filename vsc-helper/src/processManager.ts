import * as vscode from 'vscode'; //Imports
import * as cp from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { spawn } from 'child_process';

let slimeProcess: cp.ChildProcess | undefined;

export function startSlime(context: vscode.ExtensionContext) {

  const config = vscode.workspace.getConfiguration('slime');
  let slimePath = config.get<string>('exePath');

  const bundledPath = path.join(context.extensionPath, 'slime-app', 'SlimeHelper.exe');

  if (!slimePath ||
    slimePath === "C:\\SlimeHelper\\SlimeHelper.exe" ||
    slimePath === "AUTO" ||
    !fs.existsSync(slimePath)) {
    slimePath = bundledPath;
  }

  console.log(`Starting Slime from: ${slimePath}`);

  if (fs.existsSync(slimePath)) {
    try {
      slimeProcess = cp.spawn(slimePath, [process.pid.toString()], {
        detached: true,
        stdio: 'ignore'
      });

      slimeProcess.unref();
      console.log(`Slime started successfully! PID: ${slimeProcess.pid}`);
    } catch (err) {
      vscode.window.showErrorMessage(`Failed to launch Slime: ${err}`);
    }
  } else {
    vscode.window.showErrorMessage(
      `Slime Helper not found! I checked: ${slimePath}. ` +
      `Make sure to include the 'slime-app' folder in your extension package.`
    );
  }
}

//kills the software
export function stopSlime() {
  if (slimeProcess && slimeProcess.pid) {
    console.log(`Shutting down Slime process: ${slimeProcess.pid}`);

    try {
      // Taskkill /T ser till att även eventuella barn-processer stängs ner
      cp.exec(`taskkill /PID ${slimeProcess.pid} /T /F`, (err) => {
        if (err) {
          console.log("Slime process was already closed.");
        }
      });
    } catch (e) {
      console.error("Error during Slime shutdown:", e);
    }
  }
}