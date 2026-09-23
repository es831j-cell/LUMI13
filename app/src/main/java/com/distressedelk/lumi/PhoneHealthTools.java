package com.distressedelk.lumi;

import android.app.Activity;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

/** Function-tool adapter that lets Lumi's reasoning path discover and invoke Phone Health. */
final class PhoneHealthTools {
    private PhoneHealthTools() {}

    static JSONArray definitions() throws Exception {
        JSONArray a = new JSONArray();
        a.put(tool("check_phone_health",
                "Run Lumi Phone Health. Use this when the owner asks to check, scan, diagnose, inspect, or troubleshoot the phone. It always performs the complete scan first, then safe pre-authorized repairs, then verification. Riskier actions are only recommended and require owner approval.",
                new JSONObject().put("type", "object").put("properties", new JSONObject()).put("additionalProperties", false)));
        a.put(tool("read_phone_health_history",
                "Read recent Phone Health scan summaries and repair verification history without changing the phone.",
                new JSONObject().put("type", "object")
                        .put("properties", new JSONObject().put("limit", new JSONObject().put("type", "integer").put("minimum", 1).put("maximum", 20)))
                        .put("additionalProperties", false)));
        a.put(tool("discover_lumi_modules",
                "Read Lumi's runtime module registry so Lumi can discover installed first-class capabilities.",
                new JSONObject().put("type", "object").put("properties", new JSONObject()).put("additionalProperties", false)));
        return a;
    }

    static boolean handles(String name) {
        return "check_phone_health".equals(name) || "read_phone_health_history".equals(name) || "discover_lumi_modules".equals(name);
    }

    static String execute(Activity activity, SharedPreferences prefs, String name, JSONObject args) {
        try {
            if ("check_phone_health".equals(name)) {
                JSONObject report = PhoneHealthModule.runFullScan(activity, prefs);
                try {
                    LumiMemoryVault.get(activity).ledger("phone-health", "Full phone health scan",
                            report.optJSONObject("summary") == null ? "completed" : report.optJSONObject("summary").toString(), "");
                } catch (Throwable ignored) {}
                return report.toString();
            }
            if ("read_phone_health_history".equals(name)) {
                int limit = Math.max(1, Math.min(20, args == null ? 5 : args.optInt("limit", 5)));
                JSONArray all;
                try { all = new JSONArray(prefs.getString("phone_health_history_json", "[]")); }
                catch (Exception e) { all = new JSONArray(); }
                JSONArray selected = new JSONArray();
                for (int i = Math.max(0, all.length() - limit); i < all.length(); i++) {
                    JSONObject r = all.optJSONObject(i);
                    if (r == null) continue;
                    selected.put(new JSONObject()
                            .put("completedAt", r.optLong("completedAt", 0L))
                            .put("summary", r.optJSONObject("summary"))
                            .put("adbEscalationRequired", r.optBoolean("adbEscalationRequired", false)));
                }
                return new JSONObject().put("ok", true).put("history", selected).toString();
            }
            if ("discover_lumi_modules".equals(name)) {
                LumiModuleRegistry.initialize(activity, prefs);
                return new JSONObject().put("ok", true).put("modules", LumiModuleRegistry.discover(prefs)).toString();
            }
            return new JSONObject().put("ok", false).put("state", "UNKNOWN_PHONE_HEALTH_TOOL").toString();
        } catch (Exception e) {
            try { return new JSONObject().put("ok", false).put("state", "FAILED")
                    .put("error", e.getClass().getSimpleName() + ": " + String.valueOf(e.getMessage())).toString(); }
            catch (Exception ignored) { return "{\"ok\":false,\"state\":\"FAILED\"}"; }
        }
    }

    private static JSONObject tool(String name, String description, JSONObject parameters) throws Exception {
        return new JSONObject().put("type", "function").put("name", name).put("description", description)
                .put("parameters", parameters).put("strict", false);
    }
}
