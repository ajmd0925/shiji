package io.ajmd.assistant;

import jakarta.annotation.PostConstruct;
import javax.sql.DataSource;
import org.springframework.context.annotation.DependsOn;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.stereotype.Component;

/** Idempotent compatibility upgrade for the pre-system-notification MySQL schema. */
@Component("reminderSchema")
@DependsOn("entityManagerFactory")
class ReminderSchema {
    private final DataSource source;
    ReminderSchema(DataSource source) { this.source=source; }
    @PostConstruct void migrate() throws java.sql.SQLException {
        try(var connection=source.getConnection()) {
            if(!connection.getMetaData().getDatabaseProductName().equals("MySQL")) return;
        }
        var jdbc=new JdbcTemplate(source);
        var types=jdbc.queryForList("SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='assistant_reminders' AND COLUMN_NAME='state'",String.class);
        if(types.size()==1 && types.getFirst().equalsIgnoreCase("enum")) {
            // ENUM -> VARCHAR preserves existing values and supports future state additions.
            jdbc.execute("ALTER TABLE assistant_reminders MODIFY COLUMN state VARCHAR(32) NOT NULL");
        }
    }
}
