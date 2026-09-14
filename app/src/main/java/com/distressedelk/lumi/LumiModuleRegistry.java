package com.distressedelk.lumi;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.Locale;

/**
 * First-class registry for capabilities Lumi Core can discover at runtime.
 * Modules expose identity, natural-language aliases, callable actions, and authority boundaries.
 */
final class LumiModuleRegistry {
    private static final String PREF_MODULES = "lumi_modules_json";
    private static final String PREF_VERSION = "lumi_module_registry_version";
    private static final int VERSION = 1;

    private LumiModuleRegistry() {}

    static synchronized void initialize(Context context, SharedPreferences prefs) {
        try {
            JSONArray modules = new JSONArray();
            modules.put(PhoneHealthModule.descriptor());
            prefs.edit()
                    .putString(PREF_MODULES, modules.toString())
                    .putInt(PREF_VERSION, VERSION)
                    .putLong("lumi_module_registry_initialized_at", System.currentTimeMillis())
                    .apply();
        } catch (Exception e) {
            prefs.edit().putString("lumi_module_registry_error",
                    e.getClass().getSimpleName() + ": " + String.valueOf(e.getMessage())).apply();
        }
    }

    static JSONArray discover(SharedPreferences prefs) {
        try { return new JSONArray(prefs.getString(PREF_MODULES, "[]")); }
        catch (Exception e) { return new JSONArray(); }
    }

    static JSONObject findById(SharedPreferences prefs, String id) {
        if (id == null) return null;
        JSONArray modules = discover(prefs);
        for (int i = 0; i < modules.length(); i++) {
            JSONObject m = modules.optJSONObject(i);
            if (m != null && id.equalsIgnoreCase(m.optString("id", ""))) return m;
        }
        return null;
    }

    static JSONObject findByNaturalLanguage(SharedPreferences prefs, String query) {
        if (query == null) return null;
        String q = query.toLowerCase(Locale.US).trim();
        JSONArray modules = discover(prefs);
        for (int i = 0; i < modules.length(); i++) {
            JSONObject m = modules.optJSONObject(i);
            if (m == null || !m.optBoolean("enabled", true)) continue;
            JSONArray aliases = m.optJSONArray("aliases");
            if (aliases == null) continue;
            for (int a = 0; a < aliases.length(); a++) {
                String alias = aliases.optString(a, "").toLowerCase(Locale.US).trim();
                if (!alias.isEmpty() && q.contains(alias)) return m;
            }
        }
        return null;
    }
}
