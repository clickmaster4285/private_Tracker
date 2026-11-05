/**
 * Client commands the admin hands to the operator of an employee machine.
 *
 * "Remove Current Device" (server side) only revokes the device registration — the
 * machine keeps its local account (credentials, device token, collected rows) until
 * the desktop client's `--remove-account` command runs on it. That command wipes
 * every locally stored row and returns the tool to its sign-in page, which is what
 * completes the handover of a machine.
 *
 * Paths mirror what the build scripts install — keep them in sync when the install
 * layout changes (Installer-Parity Rule):
 *   Linux   — `client/publish/build-deb.sh` installs the binary at
 *             `/usr/share/$PACKAGE_NAME/client` and symlinks it as
 *             `/usr/bin/$PACKAGE_NAME` (PACKAGE_NAME = "alpha-ai-tracker", from
 *             `client/APP_IDENTIFIERS`), so the bare command works from any shell.
 *   Windows — `client/publish/installer-windows.iss` installs to
 *             `{autopf}\{#MyAppName}` = `C:\Program Files (x86)\Alpha AI Tracker`
 *             with `{#MyAppExeName}` = `client.exe`. Inno's `{autopf}` resolves to
 *             the 32-bit Program Files folder because the script does not set
 *             `ArchitecturesInstallIn64BitMode`.
 *
 * There is no server round-trip for these strings: they are packaging facts of the
 * desktop client, not workspace data.
 */
export interface RemoveAccountCommand {
  /** Install target the command belongs to. */
  os: 'Linux' | 'Windows';
  /** Shell label shown next to the command. */
  shell: string;
  /** Copy-paste-ready command (exactly what the operator runs). */
  command: string;
}

/**
 * Local account wipe commands. When `code` is set (Fixed / org-default Fixed),
 * appends `--code` — required by the client P1 lockdown.
 */
export function removeAccountCommands(code?: string): RemoveAccountCommand[] {
  const safe = (code ?? '').replace(/"/g, '');
  const withCode = safe.length > 0;
  const codeArg = withCode ? ` --code "${safe}"` : '';
  return [
    {
      os: 'Linux',
      shell: 'Terminal',
      command: `alpha-ai-tracker --remove-account${codeArg}`,
    },
    {
      os: 'Windows',
      shell: 'PowerShell',
      // client.exe is a console subsystem binary so `& …` waits for completion
      // (spinner + success/fail). Add --no-pause to skip the final Enter prompt.
      command:
        `& "C:\\Program Files (x86)\\Alpha AI Tracker\\client.exe" --remove-account${codeArg} --no-pause`,
    },
  ];
}

/** Plain `--remove-account` (Roaming only). Fixed devices need {@link removeAccountCommands} with a code. */
export const REMOVE_ACCOUNT_COMMANDS: RemoveAccountCommand[] = removeAccountCommands();

export interface ProtectedUninstallCommand {
  /** Install target the command belongs to. */
  os: 'Linux' | 'Windows';
  /** Shell label shown next to the command. */
  shell: string;
  /** Copy-paste-ready command (exactly what the operator runs). */
  command: string;
}

/**
 * Protected uninstall / removal-code commands (Linux + Windows).
 * Requires a one-time server-issued removal code from Issue Removal Code.
 */
export function protectedUninstallCommands(code: string): ProtectedUninstallCommand[] {
  // Double quotes work in bash, cmd.exe, and PowerShell. Single quotes are LITERAL
  // in cmd (`--code 'XYZ'` sends the quote characters and verify returns 401).
  const safe = code.replace(/"/g, '');
  return [
    {
      os: 'Linux',
      shell: 'Terminal',
      command: `alpha-ai-tracker --protected-uninstall --code "${safe}"`,
    },
    {
      os: 'Windows',
      shell: 'PowerShell',
      command:
        `"C:\\Program Files (x86)\\Alpha AI Tracker\\client.exe" --protected-uninstall --code "${safe}" --no-pause`,
    },
  ];
}

/** @deprecated Prefer protectedUninstallCommands — kept for callers that only need Windows. */
export function protectedUninstallCommand(code: string): string {
  return protectedUninstallCommands(code).find((c) => c.os === 'Windows')!.command;
}
