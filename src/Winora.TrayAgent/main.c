// Windows 11 stores per-app tray visibility here. This is an undocumented shell
// preference, not a Shell_NotifyIcon flag or the obsolete EnableAutoTray switch.
#ifndef UNICODE
#define UNICODE
#endif
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <wchar.h>

static const wchar_t *trayPath = L"Control Panel\\NotifyIconSettings";
static const wchar_t *preferencePath = L"Software\\Winora\\TrayIcons\\Preference";
static const wchar_t *statePath = L"Software\\Winora\\TrayIcons\\State";

static DWORD readDword(HKEY key, const wchar_t *name, DWORD fallback) {
    DWORD value = 0, type = 0, size = sizeof(value);
    return RegQueryValueExW(key, name, NULL, &type, (BYTE *)&value, &size) == ERROR_SUCCESS
        && type == REG_DWORD && size == sizeof(value) ? value : fallback;
}
static void report(HKEY state, DWORD error, DWORD promoted) {
    DWORD pid = GetCurrentProcessId();
    RegSetValueExW(state, L"LastError", 0, REG_DWORD, (BYTE *)&error, sizeof(error));
    RegSetValueExW(state, L"PromotedCount", 0, REG_DWORD, (BYTE *)&promoted, sizeof(promoted));
    RegSetValueExW(state, L"ProcessId", 0, REG_DWORD, (BYTE *)&pid, sizeof(pid));
}
static DWORD promoteAll(HKEY root, DWORD *promoted) {
    DWORD firstError = ERROR_SUCCESS;
    *promoted = 0;
    for (DWORD index = 0;; index++) {
        wchar_t name[256];
        DWORD length = ARRAYSIZE(name);
        LSTATUS error = RegEnumKeyExW(root, index, name, &length, NULL, NULL, NULL, NULL);
        if (error == ERROR_NO_MORE_ITEMS) break;
        if (error != ERROR_SUCCESS) return (DWORD)error;
        HKEY entry = NULL;
        error = RegOpenKeyExW(root, name, 0, KEY_QUERY_VALUE | KEY_SET_VALUE, &entry);
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_KEY_DELETED) continue;
        if (error == ERROR_SUCCESS) {
            if (readDword(entry, L"IsPromoted", 0) != 1) {
                DWORD visible = 1;
                error = RegSetValueExW(entry, L"IsPromoted", 0, REG_DWORD, (BYTE *)&visible, sizeof(visible));
                if (error == ERROR_SUCCESS) (*promoted)++;
            }
            RegCloseKey(entry);
        }
        if (error != ERROR_SUCCESS && firstError == ERROR_SUCCESS) firstError = (DWORD)error;
    }
    return firstError;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR command, int show) {
    (void)instance; (void)previous; (void)command; (void)show;
    wchar_t testTray[512], testPreference[512], testState[512], testMutex[512];
    int argc = 0;
    wchar_t **argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    const wchar_t *mutexName = L"Local\\Winora.TrayIcons";
    if (argc == 3 && wcscmp(argv[1], L"--test") == 0) {
        // Test runs are restricted to a disposable Winora registry namespace.
        if (wcsncmp(argv[2], L"Software\\Winora\\Tests\\", 22) != 0 || wcslen(argv[2]) > 400) {
            LocalFree(argv); return ERROR_INVALID_PARAMETER;
        }
        swprintf(testTray, ARRAYSIZE(testTray), L"%ls\\Entries", argv[2]);
        swprintf(testPreference, ARRAYSIZE(testPreference), L"%ls\\Preference", argv[2]);
        swprintf(testState, ARRAYSIZE(testState), L"%ls\\State", argv[2]);
        trayPath = testTray; preferencePath = testPreference; statePath = testState;
        swprintf(testMutex, ARRAYSIZE(testMutex), L"Local\\Winora.TrayTest.%ls", wcsrchr(argv[2], L'\\') + 1);
        mutexName = testMutex;
    } else if (argc != 1) { LocalFree(argv); return ERROR_INVALID_PARAMETER; }
    HANDLE mutex = CreateMutexW(NULL, TRUE, mutexName);
    if (!mutex) { LocalFree(argv); return (int)GetLastError(); }
    if (GetLastError() == ERROR_ALREADY_EXISTS) { CloseHandle(mutex); LocalFree(argv); return 0; }
    SetPriorityClass(GetCurrentProcess(), IDLE_PRIORITY_CLASS);
    HKEY preference = NULL, state = NULL;
    DWORD error = (DWORD)RegOpenKeyExW(HKEY_CURRENT_USER, preferencePath, 0, KEY_READ | KEY_NOTIFY, &preference);
    if (error == ERROR_SUCCESS)
        error = (DWORD)RegCreateKeyExW(HKEY_CURRENT_USER, statePath, 0, NULL, 0, KEY_SET_VALUE, NULL, &state, NULL);
    HANDLE preferenceChanged = CreateEventW(NULL, FALSE, FALSE, NULL);
    HANDLE trayChanged = CreateEventW(NULL, FALSE, FALSE, NULL);
    HKEY tray = NULL;
    if (!preferenceChanged || !trayChanged) error = GetLastError();
    while (error == ERROR_SUCCESS) {
        // Arm notifications before reading/scanning, so registrations racing a scan
        // and our own writes are covered. Only writes that change a value are made.
        error = (DWORD)RegNotifyChangeKeyValue(preference, FALSE, REG_NOTIFY_CHANGE_LAST_SET, preferenceChanged, TRUE);
        if (error != ERROR_SUCCESS || readDword(preference, L"Enabled", 0) != 1) break;
        if (!tray) {
            error = (DWORD)RegOpenKeyExW(HKEY_CURRENT_USER, trayPath, 0, KEY_READ | KEY_NOTIFY, &tray);
            if (error != ERROR_SUCCESS) {
                report(state, error, 0);
                WaitForSingleObject(preferenceChanged, 5000);
                RegCloseKey(preference); preference = NULL;
                error = (DWORD)RegOpenKeyExW(HKEY_CURRENT_USER, preferencePath, 0, KEY_READ | KEY_NOTIFY, &preference);
                ResetEvent(preferenceChanged);
                continue;
            }
        }
        error = (DWORD)RegNotifyChangeKeyValue(tray, TRUE,
            REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, trayChanged, TRUE);
        if (error != ERROR_SUCCESS) {
            report(state, error, 0); RegCloseKey(tray); tray = NULL;
            WaitForSingleObject(preferenceChanged, 1000);
            RegCloseKey(preference); preference = NULL;
            error = (DWORD)RegOpenKeyExW(HKEY_CURRENT_USER, preferencePath, 0, KEY_READ | KEY_NOTIFY, &preference);
            ResetEvent(preferenceChanged); continue;
        }
        DWORD promoted = 0;
        DWORD scanError = promoteAll(tray, &promoted);
        report(state, scanError, promoted);
        HANDLE events[] = { preferenceChanged, trayChanged };
        DWORD wait = WaitForMultipleObjects(2, events, FALSE, INFINITE);
        if (wait == WAIT_FAILED) { error = GetLastError(); break; }
        // One subscription per key: cancel the outstanding subscription by closing
        // the handle, then reopen it. This also handles Explorer replacing the root.
        RegCloseKey(tray); tray = NULL;
        RegCloseKey(preference); preference = NULL;
        error = (DWORD)RegOpenKeyExW(HKEY_CURRENT_USER, preferencePath, 0, KEY_READ | KEY_NOTIFY, &preference);
        ResetEvent(preferenceChanged); ResetEvent(trayChanged);
        // Coalesce bursts (and avoid spinning if another program fights the preference).
        Sleep(100);
    }
    if (state) { report(state, error, 0); DWORD zero = 0;
        RegSetValueExW(state, L"ProcessId", 0, REG_DWORD, (BYTE *)&zero, sizeof(zero)); RegCloseKey(state); }
    if (tray) RegCloseKey(tray);
    if (preference) RegCloseKey(preference);
    if (preferenceChanged) CloseHandle(preferenceChanged);
    if (trayChanged) CloseHandle(trayChanged);
    ReleaseMutex(mutex); CloseHandle(mutex); LocalFree(argv);
    return (int)error;
}
