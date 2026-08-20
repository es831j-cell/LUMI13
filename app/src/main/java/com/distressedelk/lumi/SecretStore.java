package com.distressedelk.lumi;

import android.content.SharedPreferences;

final class SecretStore {
    private static final String PREFIX = "secure_";
    private SecretStore() {}

    static void migratePrototypeSecrets(SharedPreferences prefs) {
        migrateOne(prefs, "openai_api_key");
        migrateOne(prefs, "opensource_api_key");
    }

    private static void migrateOne(SharedPreferences prefs, String key) {
        String plain = prefs.getString(key, "");
        if (plain != null && !plain.trim().isEmpty()) {
            PrivateStore.write(prefs, PREFIX + key, plain.trim());
            prefs.edit().remove(key).apply();
        }
    }

    static String get(SharedPreferences prefs, String key) { return PrivateStore.read(prefs, PREFIX + key); }
    static void put(SharedPreferences prefs, String key, String value) {
        String v = value == null ? "" : value.trim();
        if (v.isEmpty()) clear(prefs, key); else PrivateStore.write(prefs, PREFIX + key, v);
        prefs.edit().remove(key).apply();
    }
    static void clear(SharedPreferences prefs, String key) { prefs.edit().remove(PREFIX + key).remove(key).apply(); }
}
