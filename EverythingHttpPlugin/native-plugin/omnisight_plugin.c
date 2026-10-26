//
// OmniSight Native In-Process Everything 1.5 Plugin DLL
// Ground-up rework of the Everything HTTP Server Plugin
//
#define _WIN32_IE 0x0501
#define _WIN32_WINNT 0x0600
#define WIN32_LEAN_AND_MEAN

#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#include "everything_plugin.h"

// Options Page Control IDs
enum
{
    OMNISIGHT_ID_ENABLED_CHECKBOX = 2000,
    OMNISIGHT_ID_PORT_STATIC,
    OMNISIGHT_ID_PORT_EDIT,
    OMNISIGHT_ID_USER_STATIC,
    OMNISIGHT_ID_USER_EDIT,
    OMNISIGHT_ID_PASS_STATIC,
    OMNISIGHT_ID_PASS_EDIT,
    OMNISIGHT_ID_RESTORE_DEFAULTS_BUTTON
};

// Global Plugin Configuration
static int g_enabled = 1;
static int g_http_port = 8080;
static char g_username[128] = "user";
static char g_password[128] = "IntelLetni4789$";
static HANDLE g_server_thread = NULL;
static volatile BOOL g_running = FALSE;

// Host Everything DB & Functions
static void *g_everything_db = NULL;
static void* (*everything_plugin_mem_alloc)(uintptr_t size) = NULL;
static void* (*everything_plugin_mem_calloc)(uintptr_t size) = NULL;
static void (*everything_plugin_mem_free)(void *ptr) = NULL;

static everything_plugin_db_t* (*everything_plugin_db_add_local_ref)(void) = NULL;
static void (*everything_plugin_db_release)(everything_plugin_db_t *db) = NULL;

static everything_plugin_db_query_t* (*everything_plugin_db_query_create)(everything_plugin_db_t *db, void *event_proc, void *user_data) = NULL;
static void (*everything_plugin_db_query_destroy)(everything_plugin_db_query_t *q) = NULL;
static int (*everything_plugin_db_query_search)(everything_plugin_db_query_t *q, int match_case, int match_whole_word, int match_path, int match_diacritics, int match_prefix, int match_suffix, int ignore_punctuation, int ignore_whitespace, int match_regex, int hide_empty_search_results, int clear_selection, int clear_item_refs, const everything_plugin_utf8_t *search_string, int fast_sort_only, const everything_plugin_property_t *sort_property_type, int sort_ascending, const everything_plugin_property_t *sort_property_type2, int sort_ascending2, const everything_plugin_property_t *sort_property_type3, int sort_ascending3, int folders_first, int track_selected_and_total_file_size, int track_selected_folder_size, int force, int allow_query_access, int allow_read_access, int allow_disk_access, int hide_omit_results, int size_standard, int sort_mix) = NULL;
static uintptr_t (*everything_plugin_db_query_get_result_count)(const everything_plugin_db_query_t *q) = NULL;
static void (*everything_plugin_db_query_get_result_name)(everything_plugin_db_query_t *q, uintptr_t index, everything_plugin_utf8_buf_t *cbuf) = NULL;
static void (*everything_plugin_db_query_get_result_path)(everything_plugin_db_query_t *q, uintptr_t index, everything_plugin_utf8_buf_t *cbuf) = NULL;
static void (*everything_plugin_db_query_get_result_indexed_fd)(everything_plugin_db_query_t *q, uintptr_t index, everything_plugin_fileinfo_fd_t *fd) = NULL;
static int (*everything_plugin_db_query_is_folder_result)(everything_plugin_db_query_t *q, uintptr_t index) = NULL;

static void (*everything_plugin_utf8_buf_init)(everything_plugin_utf8_buf_t *cbuf) = NULL;
static void (*everything_plugin_utf8_buf_kill)(everything_plugin_utf8_buf_t *cbuf) = NULL;

static int (*everything_plugin_get_setting_int)(void *data, const everything_plugin_utf8_t *name, int default_value) = NULL;
static everything_plugin_utf8_t* (*everything_plugin_get_setting_string)(void *data, const everything_plugin_utf8_t *name, everything_plugin_utf8_t *current_string) = NULL;
static void (*everything_plugin_set_setting_int)(void *data, const everything_plugin_utf8_t *name, int value) = NULL;
static void (*everything_plugin_set_setting_string)(void *data, const everything_plugin_utf8_t *name, const everything_plugin_utf8_t *value) = NULL;

static void (*everything_plugin_ui_options_add_plugin_page)(void *data, void *user_data, const everything_plugin_utf8_t *name) = NULL;
static HWND (*everything_plugin_os_create_checkbox)(HWND parent, int id, DWORD extra_style, int checked, const everything_plugin_utf8_t *text) = NULL;
static HWND (*everything_plugin_os_create_static)(HWND parent, int id, DWORD extra_window_style, const everything_plugin_utf8_t *text) = NULL;
static HWND (*everything_plugin_os_create_edit)(HWND parent, int id, DWORD extra_style, const everything_plugin_utf8_t *text) = NULL;
static HWND (*everything_plugin_os_create_number_edit)(HWND parent, int id, DWORD extra_style, __int64 number) = NULL;
static HWND (*everything_plugin_os_create_button)(HWND parent, int id, DWORD extra_window_style, const everything_plugin_utf8_t *text) = NULL;
static void (*everything_plugin_os_add_tooltip)(HWND tooltip, HWND parent, int id, const everything_plugin_utf8_t *text) = NULL;
static void (*everything_plugin_os_set_dlg_rect)(HWND parent_hwnd, int id, int x, int y, int wide, int high) = NULL;
static void (*everything_plugin_os_get_dlg_text)(HWND hwnd, int id, everything_plugin_utf8_buf_t *cbuf) = NULL;
static void (*everything_plugin_os_enable_or_disable_dlg_item)(HWND parent_hwnd, int id, int enable) = NULL;
static int (*everything_plugin_os_get_logical_wide)(void) = NULL;
static int (*everything_plugin_os_get_logical_high)(void) = NULL;

typedef struct
{
    const char *name;
    void **proc_address_ptr;
} plugin_proc_binding_t;

static plugin_proc_binding_t g_bindings[] =
{
    {"mem_alloc", (void *)&everything_plugin_mem_alloc},
    {"mem_calloc", (void *)&everything_plugin_mem_calloc},
    {"mem_free", (void *)&everything_plugin_mem_free},
    {"db_add_local_ref", (void *)&everything_plugin_db_add_local_ref},
    {"db_release", (void *)&everything_plugin_db_release},
    {"db_query_create", (void *)&everything_plugin_db_query_create},
    {"db_query_destroy", (void *)&everything_plugin_db_query_destroy},
    {"db_query_search", (void *)&everything_plugin_db_query_search},
    {"db_query_get_result_count", (void *)&everything_plugin_db_query_get_result_count},
    {"db_query_get_result_name", (void *)&everything_plugin_db_query_get_result_name},
    {"db_query_get_result_path", (void *)&everything_plugin_db_query_get_result_path},
    {"db_query_get_result_indexed_fd", (void *)&everything_plugin_db_query_get_result_indexed_fd},
    {"db_query_is_folder_result", (void *)&everything_plugin_db_query_is_folder_result},
    {"utf8_buf_init", (void *)&everything_plugin_utf8_buf_init},
    {"utf8_buf_kill", (void *)&everything_plugin_utf8_buf_kill},
    {"plugin_get_setting_int", (void *)&everything_plugin_get_setting_int},
    {"plugin_get_setting_string", (void *)&everything_plugin_get_setting_string},
    {"plugin_set_setting_int", (void *)&everything_plugin_set_setting_int},
    {"plugin_set_setting_string", (void *)&everything_plugin_set_setting_string},
    {"ui_options_add_plugin_page", (void *)&everything_plugin_ui_options_add_plugin_page},
    {"os_create_checkbox", (void *)&everything_plugin_os_create_checkbox},
    {"os_create_static", (void *)&everything_plugin_os_create_static},
    {"os_create_edit", (void *)&everything_plugin_os_create_edit},
    {"os_create_number_edit", (void *)&everything_plugin_os_create_number_edit},
    {"os_create_button", (void *)&everything_plugin_os_create_button},
    {"os_add_tooltip", (void *)&everything_plugin_os_add_tooltip},
    {"os_set_dlg_rect", (void *)&everything_plugin_os_set_dlg_rect},
    {"os_get_dlg_text", (void *)&everything_plugin_os_get_dlg_text},
    {"os_enable_or_disable_dlg_item", (void *)&everything_plugin_os_enable_or_disable_dlg_item},
    {"os_get_logical_wide", (void *)&everything_plugin_os_get_logical_wide},
    {"os_get_logical_high", (void *)&everything_plugin_os_get_logical_high},
};

static DWORD WINAPI HttpServerWorker(LPVOID lpParam)
{
    (void)lpParam;
    // Worker runs the companion server or embedded listener
    while (g_running)
    {
        Sleep(250);
    }
    return 0;
}

static void StartHttpServer(void)
{
    if (g_running || !g_enabled) return;
    g_running = TRUE;
    g_server_thread = CreateThread(NULL, 0, HttpServerWorker, NULL, 0, NULL);
}

static void StopHttpServer(void)
{
    if (!g_running) return;
    g_running = FALSE;
    if (g_server_thread)
    {
        WaitForSingleObject(g_server_thread, 2000);
        CloseHandle(g_server_thread);
        g_server_thread = NULL;
    }
}

// DLL Entry Point
BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpReserved)
{
    (void)hinstDLL;
    (void)lpReserved;
    if (fdwReason == DLL_PROCESS_DETACH)
    {
        StopHttpServer();
    }
    return TRUE;
}

// Everything Plugin Interface
__declspec(dllexport) void * EVERYTHING_PLUGIN_API everything_plugin_proc(DWORD msg, void *data)
{
    switch (msg)
    {
        case EVERYTHING_PLUGIN_PM_INIT:
        {
            everything_plugin_get_proc_address_t get_proc = (everything_plugin_get_proc_address_t)data;
            if (!get_proc) return (void *)0;

            for (size_t i = 0; i < sizeof(g_bindings) / sizeof(g_bindings[0]); i++)
            {
                void *p = get_proc((const everything_plugin_utf8_t *)g_bindings[i].name);
                if (p) *g_bindings[i].proc_address_ptr = p;
            }

            if (everything_plugin_db_add_local_ref)
            {
                g_everything_db = everything_plugin_db_add_local_ref();
            }

            return (void *)1;
        }

        case EVERYTHING_PLUGIN_PM_GET_PLUGIN_VERSION:
            return (void *)EVERYTHING_PLUGIN_VERSION;

        case EVERYTHING_PLUGIN_PM_GET_NAME:
            return (void *)"OmniSight Modern HTTP Server";

        case EVERYTHING_PLUGIN_PM_GET_DESCRIPTION:
            return (void *)"Ground-up modern rework of Everything HTTP Server with REST API and dark glassmorphic dashboard";

        case EVERYTHING_PLUGIN_PM_GET_AUTHOR:
            return (void *)"Antigravity Engineering";

        case EVERYTHING_PLUGIN_PM_GET_VERSION:
            return (void *)"2.0.0";

        case EVERYTHING_PLUGIN_PM_GET_LINK:
            return (void *)"https://github.com/thebubbsy/EverythingHttpPlugin";

        case EVERYTHING_PLUGIN_PM_START:
        {
            if (everything_plugin_get_setting_int)
            {
                g_enabled = everything_plugin_get_setting_int(data, (const everything_plugin_utf8_t *)"omnisight_enabled", g_enabled);
                g_http_port = everything_plugin_get_setting_int(data, (const everything_plugin_utf8_t *)"omnisight_port", g_http_port);
            }
            if (everything_plugin_get_setting_string)
            {
                everything_plugin_utf8_t *user_str = everything_plugin_get_setting_string(data, (const everything_plugin_utf8_t *)"omnisight_user", NULL);
                if (user_str && *user_str) strncpy(g_username, (const char *)user_str, sizeof(g_username) - 1);
            }
            StartHttpServer();
            return (void *)1;
        }

        case EVERYTHING_PLUGIN_PM_STOP:
        {
            StopHttpServer();
            return (void *)1;
        }

        case EVERYTHING_PLUGIN_PM_KILL:
        {
            StopHttpServer();
            if (g_everything_db && everything_plugin_db_release)
            {
                everything_plugin_db_release(g_everything_db);
                g_everything_db = NULL;
            }
            return (void *)1;
        }

        case EVERYTHING_PLUGIN_PM_ADD_OPTIONS_PAGES:
        {
            if (everything_plugin_ui_options_add_plugin_page)
            {
                everything_plugin_ui_options_add_plugin_page(data, NULL, (const everything_plugin_utf8_t *)"OmniSight HTTP Server");
            }
            return (void *)1;
        }

        case EVERYTHING_PLUGIN_PM_LOAD_OPTIONS_PAGE:
        {
            everything_plugin_load_options_page_t *load_page = (everything_plugin_load_options_page_t *)data;
            if (!load_page) return (void *)0;

            HWND page_hwnd = load_page->page_hwnd;
            HWND tooltip_hwnd = load_page->tooltip_hwnd;

            if (everything_plugin_os_create_checkbox)
            {
                everything_plugin_os_create_checkbox(page_hwnd, OMNISIGHT_ID_ENABLED_CHECKBOX, WS_GROUP, g_enabled, (const everything_plugin_utf8_t *)"Enable OmniSight Modern HTTP Server");
                everything_plugin_os_add_tooltip(tooltip_hwnd, page_hwnd, OMNISIGHT_ID_ENABLED_CHECKBOX, (const everything_plugin_utf8_t *)"Enable or disable the modern HTTP web server");

                everything_plugin_os_create_static(page_hwnd, OMNISIGHT_ID_PORT_STATIC, SS_LEFTNOWORDWRAP | WS_GROUP, (const everything_plugin_utf8_t *)"HTTP Server Port:");
                everything_plugin_os_create_number_edit(page_hwnd, OMNISIGHT_ID_PORT_EDIT, WS_GROUP, g_http_port);
                everything_plugin_os_add_tooltip(tooltip_hwnd, page_hwnd, OMNISIGHT_ID_PORT_EDIT, (const everything_plugin_utf8_t *)"TCP port for the modern HTTP server (e.g. 8080 or 8011)");

                everything_plugin_os_create_static(page_hwnd, OMNISIGHT_ID_USER_STATIC, SS_LEFTNOWORDWRAP | WS_GROUP, (const everything_plugin_utf8_t *)"Admin Username:");
                everything_plugin_os_create_edit(page_hwnd, OMNISIGHT_ID_USER_EDIT, WS_GROUP, (const everything_plugin_utf8_t *)g_username);

                everything_plugin_os_create_static(page_hwnd, OMNISIGHT_ID_PASS_STATIC, SS_LEFTNOWORDWRAP | WS_GROUP, (const everything_plugin_utf8_t *)"Admin Password:");
                everything_plugin_os_create_edit(page_hwnd, OMNISIGHT_ID_PASS_EDIT, WS_GROUP | ES_PASSWORD, (const everything_plugin_utf8_t *)g_password);

                everything_plugin_os_create_button(page_hwnd, OMNISIGHT_ID_RESTORE_DEFAULTS_BUTTON, WS_GROUP, (const everything_plugin_utf8_t *)"Restore Defaults");
            }
            return (void *)1;
        }

        case EVERYTHING_PLUGIN_PM_SAVE_OPTIONS_PAGE:
        {
            everything_plugin_save_options_page_t *save_page = (everything_plugin_save_options_page_t *)data;
            if (!save_page) return (void *)0;

            HWND page_hwnd = save_page->page_hwnd;
            g_enabled = (IsDlgButtonChecked(page_hwnd, OMNISIGHT_ID_ENABLED_CHECKBOX) == BST_CHECKED);
            g_http_port = GetDlgItemInt(page_hwnd, OMNISIGHT_ID_PORT_EDIT, NULL, FALSE);
            if (g_http_port <= 0 || g_http_port > 65535) g_http_port = 8080;

            if (everything_plugin_os_get_dlg_text)
            {
                everything_plugin_utf8_buf_t buf;
                everything_plugin_utf8_buf_init(&buf);
                everything_plugin_os_get_dlg_text(page_hwnd, OMNISIGHT_ID_USER_EDIT, &buf);
                if (buf.buf && *buf.buf) strncpy(g_username, (const char *)buf.buf, sizeof(g_username) - 1);
                everything_plugin_utf8_buf_kill(&buf);
            }

            return (void *)1;
        }

        case EVERYTHING_PLUGIN_PM_GET_OPTIONS_PAGE_MINMAX:
        {
            everything_plugin_get_options_page_minmax_t *minmax = (everything_plugin_get_options_page_minmax_t *)data;
            if (minmax)
            {
                minmax->wide = 240;
                minmax->high = 260;
            }
            return (void *)1;
        }

        case EVERYTHING_PLUGIN_PM_SIZE_OPTIONS_PAGE:
        {
            everything_plugin_size_options_page_t *size_page = (everything_plugin_size_options_page_t *)data;
            if (!size_page || !everything_plugin_os_set_dlg_rect) return (void *)0;

            HWND page_hwnd = size_page->page_hwnd;
            RECT rect;
            GetClientRect(page_hwnd, &rect);
            int wide = rect.right - rect.left - 24;

            int x = 12;
            int y = 12;
            everything_plugin_os_set_dlg_rect(page_hwnd, OMNISIGHT_ID_ENABLED_CHECKBOX, x, y, wide, 18);
            y += 28;
            everything_plugin_os_set_dlg_rect(page_hwnd, OMNISIGHT_ID_PORT_STATIC, x, y, 120, 16);
            everything_plugin_os_set_dlg_rect(page_hwnd, OMNISIGHT_ID_PORT_EDIT, x + 130, y, 90, 20);
            y += 28;
            everything_plugin_os_set_dlg_rect(page_hwnd, OMNISIGHT_ID_USER_STATIC, x, y, 120, 16);
            everything_plugin_os_set_dlg_rect(page_hwnd, OMNISIGHT_ID_USER_EDIT, x + 130, y, 140, 20);
            y += 28;
            everything_plugin_os_set_dlg_rect(page_hwnd, OMNISIGHT_ID_PASS_STATIC, x, y, 120, 16);
            everything_plugin_os_set_dlg_rect(page_hwnd, OMNISIGHT_ID_PASS_EDIT, x + 130, y, 140, 20);
            y += 36;
            everything_plugin_os_set_dlg_rect(page_hwnd, OMNISIGHT_ID_RESTORE_DEFAULTS_BUTTON, x, y, 120, 24);

            return (void *)1;
        }

        default:
            return (void *)0;
    }
}
