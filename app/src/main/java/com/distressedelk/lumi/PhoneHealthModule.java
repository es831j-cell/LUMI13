package com.distressedelk.lumi;

import android.Manifest;
import android.app.ActivityManager;
import android.app.AppOpsManager;
import android.app.NotificationManager;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothManager;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.os.BatteryManager;
import android.os.Build;
import android.os.Environment;
import android.os.PowerManager;
import android.os.StatFs;
import android.provider.Settings;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;

/**
 * Lumi Phone Health V1.
 *
 * Contract: full scan first, then a separate safe-repair pass, then verification.
 * Risky/system-level actions are never performed here without owner approval.
 */
final class PhoneHealthModule {
    static final String ID = "phone-health";
    static final String ACTION_FULL_SCAN = "com.distressedelk.lumi.action.PHONE_HEALTH_FULL_SCAN";
    static final String ACTION_DISCOVER = "com.distressedelk.lumi.action.DISCOVER_MODULES";
    static final int MAX_SAFE_REPAIR_ATTEMPTS = 3;
    private static final int HISTORY_LIMIT = 20;
    private static final int SCHEMA = 1;

    private PhoneHealthModule() {}

    static JSONObject descriptor() throws Exception {
        return new JSONObject()
                .put("id", ID)
                .put("name", "Phone Health")
                .put("version", 1)
                .put("enabled", true)
                .put("owner", "Lumi Core")
                .put("purpose", "Inspect the Android phone, diagnose issues, apply safe reversible repairs after the full scan, and verify results.")
                .put("aliases", new JSONArray()
                        .put("check the phone")
                        .put("check my phone")
                        .put("phone health")
                        .put("scan the phone")
                        .put("diagnose the phone")
                        .put("phone diagnostic"))
                .put("actions", new JSONArray().put("full_scan").put("safe_repair").put("verify").put("history"))
                .put("severity", new JSONArray().put("Info").put("Warning").put("Critical"))
                .put("scanPolicy", "owner_requested_full_scan")
                .put("repairPolicy", "finish_scan_then_apply_safe_repairs_then_verify")
                .put("maxSafeRepairAttempts", MAX_SAFE_REPAIR_ATTEMPTS)
                .put("adbEscalation", "ask_owner_after_safe_repairs_fail")
                .put("authority", authorityDescriptor());
    }

    private static JSONObject authorityDescriptor() throws Exception {
        return new JSONObject()
                .put("autoSafeReversible", true)
                .put("riskySystemChangesRequireApproval", true)
                .put("rebootRequiresApproval", true)
                .put("forceStopThirdPartyRequiresApproval", true)
                .put("deleteFilesRequiresApproval", true)
                .put("closeBackgroundAppsRequiresApproval", true)
                .put("uninstallOrDisableApps", false)
                .put("updatesRequireApproval", true)
                .put("forgetWifiRequiresApproval", true)
                .put("unpairBluetoothRequiresApproval", true)
                .put("adbRequiresApprovalAfterThreeFailures", true);
    }

    static JSONObject runFullScan(Context context, SharedPreferences prefs) {
        long started = System.currentTimeMillis();
        JSONArray findings = collectFindings(context, prefs);
        JSONArray repairs = applySafeRepairs(context, prefs, findings);
        JSONArray verification = verifyRepairs(context, prefs, repairs);

        JSONObject report = new JSONObject();
        try {
            sortBySeverity(findings);
            report.put("schema", SCHEMA);
            report.put("module", ID);
            report.put("scanType", "FULL");
            report.put("startedAt", started);
            report.put("completedAt", System.currentTimeMillis());
            report.put("findings", findings);
            report.put("repairs", repairs);
            report.put("verification", verification);
            report.put("summary", summarize(findings, repairs, verification));
            report.put("adbEscalationRequired", needsAdbEscalation(repairs));
            persistReport(prefs, report);
        } catch (Exception e) {
            try { report.put("error", e.getClass().getSimpleName() + ": " + safe(e.getMessage())); } catch (Exception ignored) {}
        }
        return report;
    }

    private static JSONArray collectFindings(Context c, SharedPreferences p) {
        JSONArray out = new JSONArray();
        checkBattery(c, out);
        checkMemory(c, out);
        checkStorage(c, out);
        checkThermal(c, out);
        checkNetwork(c, out);
        checkBluetooth(c, out);
        checkPermissions(c, out);
        checkBackgroundPolicy(c, out);
        checkSecurity(c, out);
        checkLumiRuntime(c, p, out);
        checkThirdPartyVisibility(c, out);
        return out;
    }

    private static void checkBattery(Context c, JSONArray out) {
        try {
            BatteryManager bm = (BatteryManager) c.getSystemService(Context.BATTERY_SERVICE);
            if (bm == null) return;
            int pct = bm.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY);
            boolean charging = bm.isCharging();
            if (pct >= 0 && pct <= 10 && !charging)
                add(out, "battery-low", "battery", "Warning", 0.98, "Battery is at " + pct + "% and not charging.", "Low remaining charge", "Connect power when practical.", false, true, "owner_action", null);
            else addInfo(out, "battery-state", "battery", "Battery " + pct + "% • charging=" + charging);
        } catch (Throwable ignored) {}
    }

    private static void checkMemory(Context c, JSONArray out) {
        try {
            ActivityManager am = (ActivityManager) c.getSystemService(Context.ACTIVITY_SERVICE);
            if (am == null) return;
            ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
            am.getMemoryInfo(mi);
            double free = mi.totalMem > 0 ? (100.0 * mi.availMem / mi.totalMem) : -1;
            if (mi.lowMemory || (free >= 0 && free < 8.0))
                add(out, "memory-pressure", "memory", "Warning", 0.94, String.format(Locale.US, "Available memory %.1f%%; Android lowMemory=%s", free, mi.lowMemory), "System memory pressure", "Review heavy apps. Closing third-party apps requires owner approval.", false, true, "approval_required", null);
            else addInfo(out, "memory-state", "memory", String.format(Locale.US, "Available memory %.1f%%", free));
        } catch (Throwable ignored) {}
    }

    private static void checkStorage(Context c, JSONArray out) {
        try {
            StatFs fs = new StatFs(c.getFilesDir().getAbsolutePath());
            long total = fs.getTotalBytes(), avail = fs.getAvailableBytes();
            double free = total > 0 ? 100.0 * avail / total : -1;
            if (free >= 0 && free < 5.0)
                add(out, "storage-critical", "storage", "Critical", 0.99, String.format(Locale.US, "Internal free storage %.1f%%", free), "Storage nearly full", "Identify large files/apps. Any deletion requires owner approval.", false, true, "approval_required", null);
            else if (free >= 0 && free < 12.0)
                add(out, "storage-low", "storage", "Warning", 0.98, String.format(Locale.US, "Internal free storage %.1f%%", free), "Low free storage", "Review storage consumers. Any deletion requires owner approval.", false, true, "approval_required", null);
            else addInfo(out, "storage-state", "storage", String.format(Locale.US, "Internal free storage %.1f%%", free));
        } catch (Throwable ignored) {}
    }

    private static void checkThermal(Context c, JSONArray out) {
        if (Build.VERSION.SDK_INT < 29) return;
        try {
            PowerManager pm = (PowerManager) c.getSystemService(Context.POWER_SERVICE);
            if (pm == null) return;
            int s = pm.getCurrentThermalStatus();
            if (s >= PowerManager.THERMAL_STATUS_SEVERE)
                add(out, "thermal-severe", "thermal", "Critical", 0.99, "Android thermal status=" + s, "Severe thermal load", "Reduce load and allow the phone to cool before heavy maintenance.", false, true, "owner_action", null);
            else if (s >= PowerManager.THERMAL_STATUS_MODERATE)
                add(out, "thermal-moderate", "thermal", "Warning", 0.97, "Android thermal status=" + s, "Moderate thermal load", "Reduce heavy workloads until temperature normalizes.", false, true, "owner_action", null);
            else addInfo(out, "thermal-state", "thermal", "Android thermal status=" + s);
        } catch (Throwable ignored) {}
    }

    private static void checkNetwork(Context c, JSONArray out) {
        try {
            ConnectivityManager cm = (ConnectivityManager) c.getSystemService(Context.CONNECTIVITY_SERVICE);
            if (cm == null) return;
            Network n = cm.getActiveNetwork();
            NetworkCapabilities nc = n == null ? null : cm.getNetworkCapabilities(n);
            if (nc == null) {
                add(out, "network-offline", "connectivity", "Warning", 0.99, "No active Android network.", "No usable active network", "Lumi can diagnose further; destructive network resets require owner approval where applicable.", false, true, "approval_required", null);
                return;
            }
            boolean internet = nc.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET);
            boolean validated = nc.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED);
            boolean wifi = nc.hasTransport(NetworkCapabilities.TRANSPORT_WIFI);
            boolean cell = nc.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR);
            if (internet && !validated)
                add(out, "network-unvalidated", "connectivity", "Warning", 0.92, "Network advertises internet but Android validation is false.", "Captive portal, DNS, VPN, or upstream connectivity issue", "Run connectivity diagnosis. Safe reversible DNS/VPN changes may be attempted only when the platform grants control.", false, true, "approval_or_platform_control", null);
            else addInfo(out, "network-state", "connectivity", "validated=" + validated + " wifi=" + wifi + " cellular=" + cell);
        } catch (Throwable ignored) {}
    }

    private static void checkBluetooth(Context c, JSONArray out) {
        try {
            BluetoothManager manager = (BluetoothManager) c.getSystemService(Context.BLUETOOTH_SERVICE);
            BluetoothAdapter a = manager == null ? null : manager.getAdapter();
            if (a == null) { addInfo(out, "bluetooth-unavailable", "connectivity", "No Bluetooth adapter reported."); return; }
            if (Build.VERSION.SDK_INT >= 31 && c.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
                add(out, "bluetooth-permission", "permissions", "Warning", 0.99, "BLUETOOTH_CONNECT is not granted.", "Lumi cannot fully inspect Bluetooth state", "Grant Bluetooth nearby-device permission.", false, true, "approval_required", null);
                return;
            }
            addInfo(out, "bluetooth-state", "connectivity", "Bluetooth enabled=" + a.isEnabled());
        } catch (SecurityException se) {
            add(out, "bluetooth-permission", "permissions", "Warning", 0.99, "Bluetooth state access was denied.", "Missing Bluetooth permission", "Grant Bluetooth permission.", false, true, "approval_required", null);
        } catch (Throwable ignored) {}
    }

    private static void checkPermissions(Context c, JSONArray out) {
        checkPermission(c, out, Manifest.permission.RECORD_AUDIO, "microphone-permission", "Microphone");
        checkPermission(c, out, Manifest.permission.CAMERA, "camera-permission", "Camera");
        if (Build.VERSION.SDK_INT >= 33) checkPermission(c, out, Manifest.permission.POST_NOTIFICATIONS, "notification-permission", "Notifications");
        if (Build.VERSION.SDK_INT >= 31) checkPermission(c, out, Manifest.permission.BLUETOOTH_CONNECT, "bluetooth-permission", "Bluetooth nearby devices");
        try {
            NotificationManager nm = (NotificationManager)c.getSystemService(Context.NOTIFICATION_SERVICE);
            if (Build.VERSION.SDK_INT >= 24 && nm != null && !nm.areNotificationsEnabled())
                add(out, "notifications-disabled", "permissions", "Warning", 0.99, "Android notifications are disabled for Lumi.", "Important health alerts may not surface", "Enable Lumi notifications in Android settings.", false, true, "approval_required", null);
        } catch (Throwable ignored) {}
    }

    private static void checkPermission(Context c, JSONArray out, String permission, String id, String label) {
        if (Build.VERSION.SDK_INT < 23) return;
        try {
            if (c.checkSelfPermission(permission) != PackageManager.PERMISSION_GRANTED)
                add(out, id, "permissions", "Warning", 0.99, label + " permission is not granted.", "Required capability is unavailable", "Ask the owner to grant " + label + " access.", false, true, "approval_required", null);
        } catch (Throwable ignored) {}
    }

    private static void checkBackgroundPolicy(Context c, JSONArray out) {
        try {
            ActivityManager am = (ActivityManager)c.getSystemService(Context.ACTIVITY_SERVICE);
            if (Build.VERSION.SDK_INT >= 28 && am != null && am.isBackgroundRestricted())
                add(out, "background-restricted", "system-policy", "Warning", 0.99, "Android reports Lumi background-restricted.", "Background policy can interrupt continuous Lumi services", "Recommend changing background restriction. System settings changes requiring a user screen still require owner interaction.", false, true, "approval_required", null);
            PowerManager pm = (PowerManager)c.getSystemService(Context.POWER_SERVICE);
            if (Build.VERSION.SDK_INT >= 23 && pm != null && !pm.isIgnoringBatteryOptimizations(c.getPackageName()))
                add(out, "battery-optimization", "system-policy", "Info", 0.95, "Lumi is subject to Android battery optimization.", "Android may defer background work", "Recommend unrestricted battery behavior if continuity problems are observed.", false, true, "approval_required", null);
        } catch (Throwable ignored) {}
    }

    private static void checkSecurity(Context c, JSONArray out) {
        try {
            int adb = Settings.Global.getInt(c.getContentResolver(), Settings.Global.ADB_ENABLED, 0);
            if (adb == 1)
                add(out, "adb-enabled", "security", "Warning", 0.99, "USB debugging/ADB is enabled.", "ADB increases maintenance capability and attack surface", "Keep enabled only when intentionally using the maintenance bridge.", false, true, "owner_review", null);
        } catch (Throwable ignored) {}
        try {
            String enabled = Settings.Secure.getString(c.getContentResolver(), Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES);
            if (enabled != null && !enabled.trim().isEmpty())
                add(out, "accessibility-services-enabled", "security", "Info", 0.85, "Enabled accessibility services: " + bounded(enabled, 700), "Accessibility services have powerful device visibility/control", "Review the list and confirm each enabled service is expected.", false, true, "owner_review", null);
        } catch (Throwable ignored) {}
        try {
            if (Build.VERSION.SDK_INT >= 26 && c.getPackageManager().canRequestPackageInstalls())
                add(out, "unknown-sources-lumi", "security", "Warning", 0.99, "Lumi is allowed to request package installs.", "Sideload/install authority is enabled for Lumi", "Keep only if required for the signed self-update path; installs still require the approved update flow.", false, true, "owner_review", null);
        } catch (Throwable ignored) {}
    }

    private static void checkLumiRuntime(Context c, SharedPreferences p, JSONArray out) {
        long core = p.getLong("core_last_started", 0L);
        if (core <= 0L)
            add(out, "core-start-unrecorded", "lumi", "Warning", 0.90, "No Lumi Core start timestamp is recorded.", "Core continuity service may not have initialized", "Restart Lumi's own Core service.", true, true, "auto_safe", "restart_core");
        if (!p.getBoolean("lumi_1_0_foundation_ready", false))
            add(out, "maintenance-foundation-not-ready", "lumi", "Warning", 0.95, "Native maintenance foundation is not marked ready.", "Lumi maintenance plumbing is incomplete", "Reinitialize Lumi's own maintenance foundation.", true, true, "auto_safe", "reinitialize_maintenance");
        if (p.getInt("phone_health_index_version", 0) != SCHEMA)
            add(out, "phone-health-index", "lumi", "Info", 0.99, "Phone Health index is missing or outdated.", "New/updated Phone Health module", "Build the local Phone Health index.", true, true, "auto_safe", "rebuild_phone_health_index");
    }

    private static void checkThirdPartyVisibility(Context c, JSONArray out) {
        boolean granted = false;
        try {
            AppOpsManager ops = (AppOpsManager)c.getSystemService(Context.APP_OPS_SERVICE);
            if (ops != null && Build.VERSION.SDK_INT >= 21) {
                int mode = ops.checkOpNoThrow(AppOpsManager.OPSTR_GET_USAGE_STATS, android.os.Process.myUid(), c.getPackageName());
                granted = mode == AppOpsManager.MODE_ALLOWED;
            }
        } catch (Throwable ignored) {}
        if (!granted)
            add(out, "usage-access-not-granted", "third-party-apps", "Info", 0.99, "Android Usage Access is not granted to Lumi.", "Third-party app health visibility is limited by Android", "Offer the owner Usage Access if deeper third-party trend analysis is desired.", false, true, "approval_required", null);
        else addInfo(out, "usage-access-ready", "third-party-apps", "Usage Access is available for third-party trend analysis.");
    }

    private static JSONArray applySafeRepairs(Context c, SharedPreferences p, JSONArray findings) {
        JSONArray repairs = new JSONArray();
        for (int i = 0; i < findings.length(); i++) {
            JSONObject f = findings.optJSONObject(i);
            if (f == null || !f.optBoolean("canLumiFix", false) || !"auto_safe".equals(f.optString("authority"))) continue;
            String action = f.optString("repairAction", "");
            if (action.isEmpty()) continue;
            JSONObject log = new JSONObject();
            try {
                log.put("findingId", f.optString("id"));
                log.put("action", action);
                log.put("beforeState", captureRepairState(c, p, action));
                boolean success = false;
                String error = "";
                int attempts = 0;
                for (int a = 1; a <= MAX_SAFE_REPAIR_ATTEMPTS && !success; a++) {
                    attempts = a;
                    try { success = performRepair(c, p, action); }
                    catch (Throwable t) { error = t.getClass().getSimpleName() + ": " + safe(t.getMessage()); }
                }
                log.put("attempts", attempts);
                log.put("executed", true);
                log.put("repairReturnedSuccess", success);
                log.put("error", error);
                log.put("afterState", captureRepairState(c, p, action));
                log.put("reversible", true);
                log.put("rollbackAction", rollbackName(action));
            } catch (Exception ignored) {}
            repairs.put(log);
        }
        return repairs;
    }

    private static JSONArray verifyRepairs(Context c, SharedPreferences p, JSONArray repairs) {
        JSONArray result = new JSONArray();
        for (int i = 0; i < repairs.length(); i++) {
            JSONObject r = repairs.optJSONObject(i);
            if (r == null) continue;
            String action = r.optString("action", "");
            boolean verified = verifyAction(c, p, action);
            JSONObject v = new JSONObject();
            try {
                v.put("findingId", r.optString("findingId"));
                v.put("action", action);
                v.put("verified", verified);
                if (!verified && r.optBoolean("reversible", false)) {
                    boolean rolledBack = rollback(c, p, action, r.optJSONObject("beforeState"));
                    v.put("rolledBack", rolledBack);
                    v.put("rollbackVerified", rolledBack && verifyRollback(c, p, action, r.optJSONObject("beforeState")));
                }
            } catch (Exception ignored) {}
            result.put(v);
        }
        return result;
    }

    private static boolean performRepair(Context c, SharedPreferences p, String action) {
        if ("rebuild_phone_health_index".equals(action)) {
            p.edit().putInt("phone_health_index_version", SCHEMA).putLong("phone_health_index_built_at", System.currentTimeMillis()).apply();
            return true;
        }
        if ("reinitialize_maintenance".equals(action)) {
            MaintenanceFoundation.initialize(c, p);
            return p.getBoolean("lumi_1_0_foundation_ready", false);
        }
        if ("restart_core".equals(action)) {
            try {
                android.content.Intent i = new android.content.Intent(c, LumiCoreService.class);
                if (Build.VERSION.SDK_INT >= 26) c.startForegroundService(i); else c.startService(i);
                return true;
            } catch (Throwable ignored) { return false; }
        }
        return false;
    }

    private static boolean verifyAction(Context c, SharedPreferences p, String action) {
        if ("rebuild_phone_health_index".equals(action)) return p.getInt("phone_health_index_version", 0) == SCHEMA;
        if ("reinitialize_maintenance".equals(action)) return p.getBoolean("lumi_1_0_foundation_ready", false);
        if ("restart_core".equals(action)) return p.getLong("core_last_started", 0L) > 0L;
        return false;
    }

    private static JSONObject captureRepairState(Context c, SharedPreferences p, String action) {
        JSONObject o = new JSONObject();
        try {
            if ("rebuild_phone_health_index".equals(action)) o.put("indexVersion", p.getInt("phone_health_index_version", 0));
            else if ("reinitialize_maintenance".equals(action)) o.put("foundationReady", p.getBoolean("lumi_1_0_foundation_ready", false));
            else if ("restart_core".equals(action)) o.put("coreLastStarted", p.getLong("core_last_started", 0L));
        } catch (Exception ignored) {}
        return o;
    }

    private static boolean rollback(Context c, SharedPreferences p, String action, JSONObject before) {
        if (before == null) return false;
        if ("rebuild_phone_health_index".equals(action)) {
            p.edit().putInt("phone_health_index_version", before.optInt("indexVersion", 0)).apply();
            return true;
        }
        // Foundation/core recovery actions are intentionally not undone because rollback would
        // deliberately re-create the unhealthy state. They are bounded to Lumi's own runtime.
        return false;
    }

    private static boolean verifyRollback(Context c, SharedPreferences p, String action, JSONObject before) {
        if (before == null) return false;
        if ("rebuild_phone_health_index".equals(action)) return p.getInt("phone_health_index_version", 0) == before.optInt("indexVersion", 0);
        return false;
    }

    private static String rollbackName(String action) {
        if ("rebuild_phone_health_index".equals(action)) return "restore_previous_phone_health_index_version";
        return "not_applicable_to_runtime_recovery";
    }

    private static boolean needsAdbEscalation(JSONArray repairs) {
        for (int i = 0; i < repairs.length(); i++) {
            JSONObject r = repairs.optJSONObject(i);
            if (r != null && r.optInt("attempts", 0) >= MAX_SAFE_REPAIR_ATTEMPTS && !r.optBoolean("repairReturnedSuccess", false)) return true;
        }
        return false;
    }

    private static JSONObject summarize(JSONArray findings, JSONArray repairs, JSONArray verification) throws Exception {
        int info = 0, warning = 0, critical = 0;
        for (int i = 0; i < findings.length(); i++) {
            String s = findings.optJSONObject(i) == null ? "" : findings.optJSONObject(i).optString("severity", "");
            if ("Critical".equals(s)) critical++; else if ("Warning".equals(s)) warning++; else info++;
        }
        int verified = 0;
        for (int i = 0; i < verification.length(); i++) if (verification.optJSONObject(i) != null && verification.optJSONObject(i).optBoolean("verified", false)) verified++;
        return new JSONObject().put("critical", critical).put("warning", warning).put("info", info)
                .put("safeRepairsAttempted", repairs.length()).put("repairsVerified", verified);
    }

    private static void persistReport(SharedPreferences p, JSONObject report) {
        try {
            JSONArray history;
            try { history = new JSONArray(p.getString("phone_health_history_json", "[]")); }
            catch (Exception e) { history = new JSONArray(); }
            history.put(report);
            while (history.length() > HISTORY_LIMIT) {
                JSONArray trimmed = new JSONArray();
                for (int i = history.length() - HISTORY_LIMIT; i < history.length(); i++) trimmed.put(history.opt(i));
                history = trimmed;
            }
            p.edit().putString("phone_health_last_report_json", report.toString())
                    .putString("phone_health_history_json", history.toString())
                    .putLong("phone_health_last_scan_at", System.currentTimeMillis()).apply();
        } catch (Exception ignored) {}
    }

    private static void sortBySeverity(JSONArray findings) {
        try {
            List<JSONObject> list = new ArrayList<>();
            for (int i = 0; i < findings.length(); i++) if (findings.optJSONObject(i) != null) list.add(findings.optJSONObject(i));
            Collections.sort(list, new Comparator<JSONObject>() {
                @Override public int compare(JSONObject a, JSONObject b) { return rank(b.optString("severity")) - rank(a.optString("severity")); }
            });
            for (int i = findings.length() - 1; i >= 0; i--) findings.remove(i);
            for (JSONObject o : list) findings.put(o);
        } catch (Throwable ignored) {}
    }

    private static int rank(String s) { return "Critical".equals(s) ? 3 : "Warning".equals(s) ? 2 : 1; }

    private static void addInfo(JSONArray out, String id, String category, String evidence) {
        add(out, id, category, "Info", 0.95, evidence, "Observed state", "No repair required unless behavior changes.", false, true, "none", null);
    }

    private static void add(JSONArray out, String id, String category, String severity, double confidence,
                            String evidence, String cause, String recommendation, boolean canFix,
                            boolean reversible, String authority, String repairAction) {
        try {
            JSONObject o = new JSONObject().put("id", id).put("category", category).put("severity", severity)
                    .put("confidence", confidence).put("evidence", evidence).put("likelyCause", cause)
                    .put("recommendedAction", recommendation).put("canLumiFix", canFix)
                    .put("reversible", reversible).put("authority", authority);
            if (repairAction != null) o.put("repairAction", repairAction);
            out.put(o);
        } catch (Exception ignored) {}
    }

    private static String bounded(String s, int max) {
        if (s == null) return "";
        return s.length() <= max ? s : s.substring(0, max);
    }

    private static String safe(String s) { return s == null ? "" : s.replace('\n', ' ').replace('\r', ' ').trim(); }
}
