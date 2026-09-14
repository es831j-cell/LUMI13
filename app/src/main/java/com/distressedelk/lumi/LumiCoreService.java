package com.distressedelk.lumi;

import android.app.*;
import android.content.*;
import android.os.*;

public class LumiCoreService extends Service {
    static final String CHANNEL="lumi_core";
    static final String ACTION_DISCOVER_MODULES="com.distressedelk.lumi.action.DISCOVER_MODULES";
    static final String ACTION_PHONE_HEALTH_SCAN="com.distressedelk.lumi.action.PHONE_HEALTH_FULL_SCAN";

    @Override public void onCreate(){
        super.onCreate();
        if(Build.VERSION.SDK_INT>=26){
            NotificationChannel ch=new NotificationChannel(CHANNEL,"Lumi continuity",NotificationManager.IMPORTANCE_LOW);
            ch.setDescription("Keeps Lumi's logical session available for device handoff.");
            NotificationManager nm=getSystemService(NotificationManager.class); if(nm!=null)nm.createNotificationChannel(ch);
        }
        Intent open=new Intent(this,MainActivity.class);
        open.putExtra(MainActivity.EXTRA_AUTO_LISTEN,true);
        PendingIntent pi=PendingIntent.getActivity(this,0,open,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        Notification.Builder b=Build.VERSION.SDK_INT>=26?new Notification.Builder(this,CHANNEL):new Notification.Builder(this);
        b.setContentTitle("Lumi is available")
         .setContentText("Tap to talk • hands-free listening")
         .setSmallIcon(android.R.drawable.ic_btn_speak_now)
         .setOngoing(true).setContentIntent(pi);
        startForeground(1401,b.build());
        SharedPreferences prefs=getSharedPreferences("lumi",MODE_PRIVATE);
        LumiModuleRegistry.initialize(this,prefs);
        prefs.edit().putLong("core_last_started",System.currentTimeMillis()).apply();
    }

    @Override public int onStartCommand(Intent intent,int flags,int startId){
        if(intent!=null){
            String action=intent.getAction();
            SharedPreferences prefs=getSharedPreferences("lumi",MODE_PRIVATE);
            if(ACTION_DISCOVER_MODULES.equals(action)){
                LumiModuleRegistry.initialize(this,prefs);
                prefs.edit().putLong("lumi_module_discovery_last_at",System.currentTimeMillis()).apply();
            }else if(ACTION_PHONE_HEALTH_SCAN.equals(action)){
                new Thread(() -> {
                    try{
                        org.json.JSONObject report=PhoneHealthModule.runFullScan(LumiCoreService.this,prefs);
                        prefs.edit().putString("phone_health_last_core_result",report.toString())
                                .putLong("phone_health_last_core_scan_at",System.currentTimeMillis()).apply();
                    }catch(Throwable t){
                        prefs.edit().putString("phone_health_last_core_error",t.getClass().getSimpleName()+": "+String.valueOf(t.getMessage()))
                                .putLong("phone_health_last_core_error_at",System.currentTimeMillis()).apply();
                    }
                },"LumiPhoneHealth").start();
            }
        }
        return START_STICKY;
    }
    @Override public android.os.IBinder onBind(Intent intent){return null;}
}
