package io.ajmd.assistant;

import jakarta.persistence.*;
import java.time.Instant;

@Entity
@Table(name="assistant_sync_state")
public class SyncState {
    @Id public int id=1;
    public boolean enabled;
    @Column(length=200) public String automationId;
    public Instant checkedAt;
    public Instant completedAt;
    public Instant leaseUntil;
    @Column(length=36) public String runId;
    @Column(length=1000) public String error;
    @Version public long version;
}
