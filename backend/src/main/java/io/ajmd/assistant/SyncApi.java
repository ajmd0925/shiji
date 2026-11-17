package io.ajmd.assistant;

import jakarta.validation.Valid;
import jakarta.validation.constraints.*;
import java.time.Instant;
import java.util.*;
import org.springframework.boot.ApplicationArguments;
import org.springframework.boot.ApplicationRunner;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Component;
import org.springframework.transaction.annotation.Transactional;
import org.springframework.web.bind.annotation.*;
import org.springframework.web.server.ResponseStatusException;
import static io.ajmd.assistant.Reminder.State.*;

@Component
class SyncInitializer implements ApplicationRunner {
    private final SyncRepository repository;
    SyncInitializer(SyncRepository repository) { this.repository=repository; }
    public void run(ApplicationArguments args) { if(!repository.existsById(1)) repository.save(new SyncState()); }
}

@RestController
@RequestMapping("/api/sync")
@Transactional
class SyncApi {
    private final SyncRepository sync;
    private final ReminderRepository reminders;
    SyncApi(SyncRepository sync,ReminderRepository reminders) { this.sync=sync; this.reminders=reminders; }
    record Config(boolean enabled,@NotBlank @Size(max=200) String automationId) {}
    record Check(@NotBlank @Size(max=200) String automationId) {}
    record Complete(@NotBlank String runId,@Size(max=1000) String error) {}
    record Status(boolean enabled,boolean recent,String automationId,Instant checkedAt,Instant completedAt,String error,int intervalSeconds) {}
    record Work(boolean acquired,String runId,List<Reminder> cancellations,List<Reminder> creations,List<Reminder> reconcile,List<Reminder> completed) {}
    @GetMapping("/status") Status status() {
        SyncState s=sync.findById(1).orElseThrow();
        return new Status(s.enabled,s.enabled && s.error==null && s.completedAt!=null && s.completedAt.isAfter(Instant.now().minusSeconds(150)),s.automationId,s.checkedAt,s.completedAt,s.error,60);
    }
    @PostMapping("/config") Status configure(@Valid @RequestBody Config input) {
        SyncState s=sync.lockState().orElseThrow();
        if(!Objects.equals(s.automationId,input.automationId())) { s.checkedAt=null; s.completedAt=null; s.leaseUntil=null; s.runId=null; }
        s.enabled=input.enabled(); s.automationId=input.automationId(); s.error=null;
        sync.saveAndFlush(s); return status();
    }
    @PostMapping("/check") Work check(@Valid @RequestBody Check input) {
        SyncState s=sync.lockState().orElseThrow(); Instant now=Instant.now();
        if(!s.enabled || !Objects.equals(s.automationId,input.automationId())) throw new ResponseStatusException(HttpStatus.CONFLICT,"同步自动化未启用或标识不符");
        if(s.leaseUntil!=null && s.leaseUntil.isAfter(now)) return new Work(false,null,List.of(),List.of(),List.of(),List.of());
        s.checkedAt=now; s.leaseUntil=now.plusSeconds(180); s.runId=UUID.randomUUID().toString(); sync.save(s);
        List<Reminder> all=reminders.findAllByOrderByScheduledAtAsc();
        return new Work(true,s.runId,all.stream().filter(r->r.state==CANCEL_PENDING).limit(20).toList(),
            all.stream().filter(r->r.state==PENDING && r.scheduledAt.isAfter(now)).limit(20).toList(),
            all.stream().filter(r->r.state==CREATING).limit(20).toList(),
            all.stream().filter(r->r.state==FIRED || r.state==RECEIVED).limit(20).toList());
    }
    @PostMapping("/complete") Status complete(@Valid @RequestBody Complete input) {
        SyncState s=sync.lockState().orElseThrow();
        if(!Objects.equals(s.runId,input.runId())) throw new ResponseStatusException(HttpStatus.CONFLICT,"本轮同步已失效");
        s.completedAt=Instant.now(); s.leaseUntil=null; s.runId=null; s.error=input.error(); sync.saveAndFlush(s); return status();
    }
}
