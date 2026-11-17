package io.ajmd.assistant;

import jakarta.persistence.*;
import java.time.Instant;
import java.util.UUID;

@Entity
@Table(name = "assistant_reminders", uniqueConstraints = @UniqueConstraint(columnNames = "requestKey"),
    indexes = @Index(name="idx_reminder_due", columnList="channel,state,scheduledAt"))
public class Reminder {
    public enum State { PENDING, CREATING, SCHEDULED, DISPATCHING, PAUSED, FIRED, RECEIVED, FAILED, DELIVERY_FAILED, CANCEL_PENDING, CANCELLED }
    public enum Channel { CODEX, SYSTEM }
    // Existing rows belong to the old Codex workflow and must never be sent by the system worker.
    @Enumerated(EnumType.STRING) @Column(columnDefinition="varchar(16) default 'CODEX'")
    public Channel channel = Channel.CODEX;
    @Id public UUID id = UUID.randomUUID();
    @Column(nullable = false) public UUID taskId;
    @Column(nullable = false, length = 160) public String title;
    @Column(nullable = false, length = 2000) public String message;
    @Column(nullable = false) public Instant scheduledAt;
    @Column(nullable = false, length = 64) public String timezone;
    @Column(nullable = false, length = 100) public String requestKey;
    @Enumerated(EnumType.STRING) @org.hibernate.annotations.JdbcTypeCode(org.hibernate.type.SqlTypes.VARCHAR)
    @Column(nullable = false,length=32) public State state = State.PENDING;
    @Column(length = 200) public String automationId;
    @Column(length = 1000) public String detail;
    public Instant firedAt;
    public Instant deliveryAttemptedAt;
    public Instant receivedAt;
    public Instant verifiedScheduledAt;
    @Column(nullable = false) public Instant createdAt = Instant.now();
    @Column(nullable = false) public Instant updatedAt = Instant.now();
    @Version public long version;
    public String getConfirmation() {
        if(channel!=Channel.SYSTEM) return null;
        if(state==State.CANCELLED) return detail;
        if(state==State.SCHEDULED) return "添加成功，将于 "+java.time.format.DateTimeFormatter.ofPattern("yyyy年MM月dd日 HH:mm:ss").withZone(java.time.ZoneId.of(timezone)).format(scheduledAt)+"（"+timezone+"）提醒你："+message;
        return detail;
    }
}
