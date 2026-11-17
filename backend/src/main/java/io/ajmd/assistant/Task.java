package io.ajmd.assistant;

import jakarta.persistence.*;
import java.time.Instant;
import java.util.UUID;

@Entity
@Table(name = "assistant_tasks")
public class Task {
    @Id public UUID id = UUID.randomUUID();
    @Column(nullable = false, length = 160) public String title;
    @Column(nullable = false, columnDefinition = "TEXT") public String description = "";
    @Column(nullable = false) public String priority = "NORMAL";
    public Instant dueAt;
    public boolean completed;
    @Column(nullable = false) public Instant createdAt = Instant.now();
    @Column(nullable = false) public Instant updatedAt = Instant.now();
    @Version public long version;
}
