package io.ajmd.assistant;

import java.time.*;
import java.util.*;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Service;
import org.springframework.transaction.support.TransactionTemplate;
import org.springframework.transaction.PlatformTransactionManager;
import org.springframework.web.bind.annotation.*;
import static io.ajmd.assistant.Reminder.State.*;

@Service
@org.springframework.context.annotation.DependsOn("reminderSchema")
class SystemReminders {
    private final ReminderRepository reminders;
    private final WindowsNotifications notifications;
    private final TransactionTemplate transaction;
    final String channel;
    private final boolean workerEnabled;
    private final long graceSeconds;
    private volatile Instant lastScanAt;
    private volatile String scanError;
    SystemReminders(ReminderRepository reminders,WindowsNotifications notifications,PlatformTransactionManager manager,
        @Value("${assistant.reminders.channel:SYSTEM}") String channel,
        @Value("${assistant.reminders.worker-enabled:true}") boolean workerEnabled,
        @Value("${assistant.reminders.catch-up-seconds:300}") long graceSeconds) {
        this.reminders=reminders; this.notifications=notifications; this.transaction=new TransactionTemplate(manager);
        if(!Set.of("CODEX","SYSTEM").contains(channel)) throw new IllegalArgumentException("Unknown reminder channel");
        this.channel=channel; this.workerEnabled=workerEnabled; this.graceSeconds=graceSeconds;
    }
    boolean isSystem() { return channel.equals("SYSTEM"); }
    void requireReady() {
        if(!workerEnabled || !isSystem()) throw new IllegalStateException("后台通知服务未启用");
        if(scanError!=null) throw new IllegalStateException("后台检查失败："+scanError);
        notifications.ready();
    }
    Map<String,Object> status() {
        var result=new LinkedHashMap<String,Object>();
        result.put("channel",channel); result.put("enabled",workerEnabled && isSystem());
        result.put("lastScanAt",lastScanAt); result.put("intervalMs",500); result.put("catchUpSeconds",graceSeconds);
        try { requireReady(); result.put("ready",true); result.put("error",null); }
        catch(IllegalStateException e) { result.put("ready",false); result.put("error",e.getMessage()); }
        return result;
    }
    @Scheduled(fixedDelay=500,initialDelayString="${assistant.reminders.scan-initial-delay-ms:1500}")
    void scan() {
        if(!workerEnabled || !isSystem()) return;
        try {
            Instant now=Instant.now();
            // A process crash during an OS submission has an unknown result. Never blindly resend it.
            for(var r:reminders.findByChannelAndStateAndUpdatedAtBefore(Reminder.Channel.SYSTEM,DISPATCHING,now.minusSeconds(60))) {
                transaction.executeWithoutResult(s->reminders.lockById(r.id).ifPresent(current->{
                    if(current.state==DISPATCHING && current.updatedAt.isBefore(now.minusSeconds(60))) {
                        current.state=DELIVERY_FAILED; current.detail="发送期间服务中断，系统是否已接收待核对；未自动重发"; current.updatedAt=Instant.now();
                    }
                }));
            }
            for(var r:reminders.findTop32ByChannelAndStateAndScheduledAtLessThanEqualOrderByScheduledAtAsc(Reminder.Channel.SYSTEM,SCHEDULED,now)) deliver(r.id);
            lastScanAt=Instant.now(); scanError=null;
        } catch(Exception e) { scanError=e.getClass().getSimpleName(); }
    }
    void deliver(UUID id) {
        Boolean claimed=transaction.execute(s->{
            Reminder r=reminders.lockById(id).orElseThrow();
            if(r.channel!=Reminder.Channel.SYSTEM || r.state!=SCHEDULED || r.scheduledAt.isAfter(Instant.now())) return false;
            r.updatedAt=Instant.now();
            if(r.scheduledAt.isBefore(r.updatedAt.minusSeconds(graceSeconds))) {
                r.state=DELIVERY_FAILED; r.detail="服务恢复时已超过补发窗口，未发送过期通知"; return false;
            }
            r.state=DISPATCHING; r.deliveryAttemptedAt=r.updatedAt;
            r.detail="到点已领取，发送前再次核对取消状态"; return true;
        });
        if(!Boolean.TRUE.equals(claimed)) return;
        transaction.executeWithoutResult(s->{
            Reminder r=reminders.lockById(id).orElseThrow();
            if(r.state!=DISPATCHING) return; // Cancellation may win between claim and send.
            r.deliveryAttemptedAt=Instant.now();
            try {
                notifications.show(r);
                r.state=FIRED; r.firedAt=Instant.now();
                r.detail="Windows 已接受通知；是否弹出横幅及用户是否收到尚未确认";
            } catch(IllegalStateException e) {
                r.state=DELIVERY_FAILED;
                String detail="通知发送失败或结果待核对，未自动重发："+e.getMessage();
                r.detail=detail.substring(0,Math.min(900,detail.length()));
            }
            r.updatedAt=Instant.now();
        });
    }
}

@RestController
@RequestMapping("/api/notifications")
class NotificationApi {
    private final SystemReminders service;
    NotificationApi(SystemReminders service) { this.service=service; }
    @GetMapping("/status") Map<String,Object> status() { return service.status(); }
}
