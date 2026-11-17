package io.ajmd.assistant;

import jakarta.persistence.LockModeType;
import java.util.*;
import org.springframework.data.jpa.repository.*;

interface TaskRepository extends JpaRepository<Task, UUID> {
    List<Task> findAllByOrderByCreatedAtDesc();
    @Lock(LockModeType.PESSIMISTIC_WRITE)
    @Query("select t from Task t where t.id = :id") Optional<Task> lockById(UUID id);
}
interface NoteRepository extends JpaRepository<Note, UUID> {
    List<Note> findAllByOrderByPinnedDescUpdatedAtDesc();
}
interface ReminderRepository extends JpaRepository<Reminder, UUID> {
    List<Reminder> findAllByOrderByScheduledAtAsc();
    List<Reminder> findByTaskId(UUID id);
    Optional<Reminder> findByRequestKey(String key);
    List<Reminder> findTop32ByChannelAndStateAndScheduledAtLessThanEqualOrderByScheduledAtAsc(
        Reminder.Channel channel, Reminder.State state, java.time.Instant due);
    List<Reminder> findByChannelAndStateAndUpdatedAtBefore(Reminder.Channel channel, Reminder.State state, java.time.Instant before);
    @Lock(LockModeType.PESSIMISTIC_WRITE)
    @Query("select r from Reminder r where r.id = :id") Optional<Reminder> lockById(UUID id);
}
interface SyncRepository extends JpaRepository<SyncState, Integer> {
    @Lock(LockModeType.PESSIMISTIC_WRITE)
    @Query("select s from SyncState s where s.id=1") Optional<SyncState> lockState();
}
