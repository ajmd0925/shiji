package io.ajmd.assistant;

import jakarta.validation.Valid;
import jakarta.validation.constraints.*;
import java.time.*;
import java.util.*;
import org.springframework.http.*;
import org.springframework.transaction.annotation.Transactional;
import org.springframework.web.bind.annotation.*;
import org.springframework.web.server.ResponseStatusException;
import static io.ajmd.assistant.Reminder.State.*;

@RestController
@RequestMapping("/api")
@Transactional
class Api {
    private final TaskRepository tasks;
    private final NoteRepository notes;
    private final ReminderRepository reminders;
    private final SystemReminders system;
    Api(TaskRepository tasks, NoteRepository notes, ReminderRepository reminders,SystemReminders system) { this.tasks=tasks; this.notes=notes; this.reminders=reminders; this.system=system; }

    record TaskInput(@NotBlank @Size(max=160) String title, @Size(max=10000) String description,
        @NotNull @Pattern(regexp="NORMAL|HIGH|LOW") String priority, Instant dueAt, boolean completed) {}
    record NoteInput(@NotBlank @Size(max=160) String title, @NotNull @Size(max=20000) String content,
        @NotNull @Pattern(regexp="LAVENDER|SAGE|PEACH|BLUE") String color, boolean pinned) {}
    record ReminderInput(@NotNull UUID taskId, @NotBlank @Size(max=2000) String message,
        @NotNull Instant scheduledAt, @NotBlank @Size(max=64) String timezone,
        @NotBlank @Size(max=100) String requestKey) {
        ReminderInput { if(scheduledAt!=null) scheduledAt=scheduledAt.truncatedTo(java.time.temporal.ChronoUnit.MILLIS); }
    }
    record QuickReminderInput(@NotBlank @Size(max=160) String title, @NotBlank @Size(max=2000) String message,
        @NotNull Instant scheduledAt, @NotBlank @Size(max=64) String timezone,
        @NotBlank @Size(max=100) String requestKey) {}

    /** A desktop quick reminder saves its task and schedule in one transaction. */
    @PostMapping("/reminders/quick") @ResponseStatus(HttpStatus.CREATED)
    Reminder quickReminder(@Valid @RequestBody QuickReminderInput input) {
        var existing=reminders.findByRequestKey(input.requestKey());
        if(existing.isPresent()) {
            Reminder r=existing.get();
            if(!r.title.equals(input.title().strip()) || !r.message.equals(input.message()) ||
                !r.scheduledAt.equals(input.scheduledAt().truncatedTo(java.time.temporal.ChronoUnit.MILLIS)) ||
                !r.timezone.equals(input.timezone())) throw conflict("请求标识已用于其他提醒");
            return r;
        }
        Task task=tasks.saveAndFlush(apply(new Task(),new TaskInput(input.title(),"","NORMAL",null,false)));
        return createReminder(new ReminderInput(task.id,input.message(),input.scheduledAt(),input.timezone(),input.requestKey()));
    }

    record ReminderEvent(@NotBlank @Pattern(regexp="SCHEDULED|PAUSED|FIRED|RECEIVED|FAILED|CANCELLED") String state,
        @Size(max=200) String automationId, @Size(max=1000) String detail, Instant verifiedScheduledAt) {}

    @GetMapping("/health") Map<String,String> health() { return Map.of("status","ok"); }
    @GetMapping("/tasks") List<Task> tasks() { return tasks.findAllByOrderByCreatedAtDesc(); }
    @PostMapping("/tasks") @ResponseStatus(HttpStatus.CREATED)
    Task createTask(@Valid @RequestBody TaskInput input) { return tasks.save(apply(new Task(),input)); }
    @PutMapping("/tasks/{id}") Task updateTask(@PathVariable UUID id,@Valid @RequestBody TaskInput input) {
        return tasks.save(apply(tasks.lockById(id).orElseThrow(Api::missing),input));
    }
    private Task apply(Task task,TaskInput input) {
        task.title=input.title().strip(); task.description=Objects.requireNonNullElse(input.description(),"");
        task.priority=input.priority(); task.dueAt=input.dueAt(); task.completed=input.completed(); task.updatedAt=Instant.now(); return task;
    }
    @DeleteMapping("/tasks/{id}") @ResponseStatus(HttpStatus.NO_CONTENT) void deleteTask(@PathVariable UUID id) {
        Task task=tasks.lockById(id).orElseThrow(Api::missing);
        if (reminders.findByTaskId(id).stream().anyMatch(r->r.state!=CANCELLED && r.state!=FAILED && r.state!=DELIVERY_FAILED))
            throw conflict("请先取消或确认关联提醒，再删除任务");
        reminders.deleteAll(reminders.findByTaskId(id)); tasks.delete(task);
    }
    @GetMapping("/notes") List<Note> notes() { return notes.findAllByOrderByPinnedDescUpdatedAtDesc(); }
    @PostMapping("/notes") @ResponseStatus(HttpStatus.CREATED) Note createNote(@Valid @RequestBody NoteInput input) { return notes.save(apply(new Note(),input)); }
    @PutMapping("/notes/{id}") Note updateNote(@PathVariable UUID id,@Valid @RequestBody NoteInput input) { return notes.save(apply(notes.findById(id).orElseThrow(Api::missing),input)); }
    private Note apply(Note note,NoteInput input) { note.title=input.title().strip(); note.content=input.content(); note.color=input.color(); note.pinned=input.pinned(); note.updatedAt=Instant.now(); return note; }
    @DeleteMapping("/notes/{id}") @ResponseStatus(HttpStatus.NO_CONTENT) void deleteNote(@PathVariable UUID id) { Note n=notes.findById(id).orElseThrow(Api::missing); notes.delete(n); }
    @GetMapping("/reminders") List<Reminder> reminders() { return reminders.findAllByOrderByScheduledAtAsc(); }
    @PostMapping("/reminders") @ResponseStatus(HttpStatus.CREATED) Reminder createReminder(@Valid @RequestBody ReminderInput input) {
        try { ZoneId.of(input.timezone()); } catch (DateTimeException e) { throw bad("时区无效"); }
        Task task=tasks.lockById(input.taskId()).orElseThrow(Api::missing);
        Optional<Reminder> existing=reminders.findByRequestKey(input.requestKey());
        if (existing.isPresent()) {
            Reminder r=existing.get();
            if (!r.taskId.equals(input.taskId()) || !r.message.equals(input.message()) || !r.scheduledAt.equals(input.scheduledAt()) || !r.timezone.equals(input.timezone())) throw conflict("请求标识已用于其他提醒");
            return r;
        }
        if(!input.scheduledAt().isAfter(Instant.now())) throw bad("提醒时间必须在未来");
        if (task.completed) throw conflict("已完成的任务不能添加提醒");
        if (reminders.findByTaskId(task.id).stream().anyMatch(r->r.state==PENDING || r.state==CREATING || r.state==SCHEDULED || r.state==DISPATCHING || r.state==PAUSED || r.state==CANCEL_PENDING)) throw conflict("该任务已有待创建或已安排的提醒");
        Reminder r=new Reminder(); r.taskId=task.id; r.title=task.title; r.message=input.message();
        if(system.isSystem()) {
            try { system.requireReady(); }
            catch(IllegalStateException e) { throw new ResponseStatusException(HttpStatus.SERVICE_UNAVAILABLE,"提醒未添加："+e.getMessage()); }
            if(!input.scheduledAt().isAfter(Instant.now())) throw bad("提醒时间已过，请重新设置");
            r.channel=Reminder.Channel.SYSTEM; r.state=SCHEDULED;
            r.detail="已由拾记后台安排一次性 Windows 系统通知";
        }
        r.scheduledAt=input.scheduledAt(); r.timezone=input.timezone(); r.requestKey=input.requestKey(); return reminders.save(r);
    }
    @PostMapping("/reminders/{id}/claim") Reminder claim(@PathVariable UUID id) {
        Reminder r=reminders.lockById(id).orElseThrow(Api::missing);
        if(r.channel==Reminder.Channel.SYSTEM) throw conflict("拾记系统通知由后台执行，无需创建 Codex 自动化");
        if(r.state!=PENDING) throw conflict("该提醒已被领取或处理，请勿重复创建原生任务");
        if(!r.scheduledAt.isAfter(Instant.now())) throw conflict("提醒时间已过，请取消并重新设置");
        r.state=CREATING; r.updatedAt=Instant.now(); return reminders.save(r);
    }
    @PostMapping("/reminders/{id}/events") Reminder event(@PathVariable UUID id,@Valid @RequestBody ReminderEvent event) {
        Reminder r=reminders.lockById(id).orElseThrow(Api::missing);
        Reminder.State next=Reminder.State.valueOf(event.state());
        if(r.channel==Reminder.Channel.SYSTEM) {
            if(next!=RECEIVED || (r.state!=FIRED && r.state!=RECEIVED)) throw conflict("系统通知执行状态由后台维护；仅在已发送后确认收到");
            r.state=RECEIVED; if(r.receivedAt==null) r.receivedAt=Instant.now();
            r.detail=event.detail(); r.updatedAt=Instant.now(); return reminders.save(r);
        }
        if (next==SCHEDULED && (event.automationId()==null || event.automationId().isBlank())) throw bad("必须提供原生提醒创建返回的标识");
        if(r.state==CANCEL_PENDING && (next==SCHEDULED || next==PAUSED)) {
            if(event.automationId()==null || event.automationId().isBlank()) throw bad("取消创建中的提醒时必须回写真实原生标识");
            if(r.automationId!=null && !r.automationId.equals(event.automationId())) throw conflict("提醒已绑定其他原生任务");
            r.automationId=event.automationId(); r.updatedAt=Instant.now();
            r.detail="创建期间已申请取消，等待 Codex 取消原生提醒"; return reminders.save(r);
        }
        if (r.state==next) {
            if (next==SCHEDULED && !Objects.equals(r.automationId,event.automationId())) throw conflict("提醒已绑定其他原生任务");
            if (event.detail()!=null) r.detail=event.detail();
            if (event.verifiedScheduledAt()!=null) r.verifiedScheduledAt=event.verifiedScheduledAt();
            r.updatedAt=Instant.now(); return reminders.save(r);
        }
        boolean allowed=switch(next) {
            case SCHEDULED -> r.state==CREATING;
            case PAUSED -> r.state==CREATING || r.state==SCHEDULED || r.state==FIRED || r.state==RECEIVED;
            case FIRED -> r.state==SCHEDULED;
            case RECEIVED -> r.state==SCHEDULED || r.state==FIRED;
            case FAILED -> r.state==PENDING || r.state==CREATING;
            case CANCELLED -> r.state==CANCEL_PENDING;
            default -> false;
        };
        if (!allowed) throw conflict("不允许从 "+r.state+" 更新为 "+next);
        r.state=next; if(next==SCHEDULED) r.automationId=event.automationId();
        if(next==PAUSED && r.automationId==null) {
            if(event.automationId()==null || event.automationId().isBlank()) throw bad("暂停原生提醒必须保留真实标识");
            r.automationId=event.automationId();
        }
        if(event.verifiedScheduledAt()!=null) r.verifiedScheduledAt=event.verifiedScheduledAt();
        if(next==FIRED) r.firedAt=Instant.now(); if(next==RECEIVED) r.receivedAt=Instant.now();
        r.detail=event.detail(); r.updatedAt=Instant.now(); return reminders.save(r);
    }
    @PostMapping("/reminders/{id}/cancel") Reminder cancel(@PathVariable UUID id) {
        Reminder r=reminders.lockById(id).orElseThrow(Api::missing);
        if(r.channel==Reminder.Channel.SYSTEM) {
            r.state=CANCELLED; r.updatedAt=Instant.now();
            r.detail=r.firedAt==null && r.deliveryAttemptedAt==null ? "该提醒通知已关闭，到点不会再提醒" : "已关闭后续通知；此前发出的通知可能仍在通知中心";
            return reminders.save(r);
        }
        if (r.state==PENDING || r.state==FAILED) r.state=CANCELLED;
        else if (r.state==CREATING || r.state==SCHEDULED || r.state==PAUSED || r.state==FIRED || r.state==RECEIVED) r.state=CANCEL_PENDING;
        r.updatedAt=Instant.now(); return reminders.save(r);
    }
    record DispatchResult(@com.fasterxml.jackson.annotation.JsonProperty("notify") boolean shouldNotify, String reason, UUID id, String message, String automationId) {}
    @PostMapping("/reminders/{id}/dispatch") DispatchResult dispatch(@PathVariable UUID id) {
        Reminder r=reminders.lockById(id).orElseThrow(Api::missing);
        if(r.channel==Reminder.Channel.SYSTEM) return new DispatchResult(false,"系统通知由拾记后台执行",r.id,null,null);
        if(r.state!=SCHEDULED) return new DispatchResult(false,"状态为 "+r.state+"，不发送提醒",r.id,null,r.automationId);
        Instant due=r.verifiedScheduledAt==null?r.scheduledAt:r.verifiedScheduledAt;
        if(Instant.now().isBefore(due)) return new DispatchResult(false,"尚未到提醒时间",r.id,null,r.automationId);
        r.state=FIRED; r.firedAt=Instant.now(); r.updatedAt=r.firedAt;
        r.detail="发送前已核对取消状态；本次执行已领取，等待用户确认送达";
        reminders.save(r);
        return new DispatchResult(true,"本次执行已领取",r.id,r.message,r.automationId);
    }
    private static ResponseStatusException missing() { return new ResponseStatusException(HttpStatus.NOT_FOUND,"记录不存在"); }
    private static ResponseStatusException conflict(String message) { return new ResponseStatusException(HttpStatus.CONFLICT,message); }
    private static ResponseStatusException bad(String message) { return new ResponseStatusException(HttpStatus.BAD_REQUEST,message); }
}
