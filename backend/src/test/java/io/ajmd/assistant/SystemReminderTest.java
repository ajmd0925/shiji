package io.ajmd.assistant;

import com.fasterxml.jackson.databind.*;
import java.time.Instant;
import java.util.*;
import java.util.concurrent.*;
import org.junit.jupiter.api.*;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.AutoConfigureMockMvc;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.test.context.bean.override.mockito.MockitoBean;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.http.MediaType;
import static org.assertj.core.api.Assertions.*;
import static org.mockito.Mockito.*;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.*;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.*;
import static io.ajmd.assistant.Reminder.State.*;

@SpringBootTest(properties={"assistant.reminders.channel=SYSTEM", "assistant.reminders.scan-initial-delay-ms=3600000",
    "assistant.token=test-token-abcdefghijklmnopqrstuvwxyz", "spring.datasource.url=${TEST_DB_URL:jdbc:h2:mem:system-test;MODE=MySQL;DB_CLOSE_DELAY=-1}",
    "spring.datasource.username=${TEST_DB_USER:sa}", "spring.datasource.password=${TEST_DB_PASSWORD:}", "spring.jpa.hibernate.ddl-auto=create-drop"})
@AutoConfigureMockMvc
class SystemReminderTest {
    @Autowired MockMvc mvc;
    @Autowired ObjectMapper json;
    @Autowired ReminderRepository reminders;
    @Autowired TaskRepository tasks;
    @Autowired SystemReminders service;
    @Autowired ReminderSchema schema;
    @Autowired javax.sql.DataSource source;
    @MockitoBean WindowsNotifications notifications;
    @BeforeEach void resetData() { reminders.deleteAll(); tasks.deleteAll(); reset(notifications); }
    JsonNode request(String path,Object data,int status) throws Exception {
        return json.readTree(mvc.perform(post(path).header("Authorization","Bearer test-token-abcdefghijklmnopqrstuvwxyz")
            .contentType(MediaType.APPLICATION_JSON).content(json.writeValueAsBytes(data)))
            .andExpect(status().is(status)).andReturn().getResponse().getContentAsString());
    }
    Map<String,Object> input() {
        Task t=new Task(); t.title="系统喝水测试"; tasks.saveAndFlush(t);
        return Map.of("taskId",t.id,"message","喝水 <b>原文</b>","scheduledAt",Instant.now().plusSeconds(120).toString(),"timezone","Asia/Shanghai","requestKey",UUID.randomUUID().toString());
    }
    Reminder scheduled() throws Exception {
        JsonNode created=request("/api/reminders",input(),201);
        return reminders.findById(UUID.fromString(created.path("id").asText())).orElseThrow();
    }
    Reminder due() throws Exception { var r=scheduled(); r.scheduledAt=Instant.now().minusSeconds(1); return reminders.saveAndFlush(r); }
    @Test void creationReturnsActualScheduleAndIsIdempotent() throws Exception {
        var data=input(); var first=request("/api/reminders",data,201);
        assertThat(first.path("channel").asText()).isEqualTo("SYSTEM");
        assertThat(first.path("state").asText()).isEqualTo("SCHEDULED");
        assertThat(first.path("confirmation").asText()).contains("添加成功","Asia/Shanghai","喝水");
        assertThat(request("/api/reminders",data,201).get("id")).isEqualTo(first.get("id"));
        verify(notifications).ready(); verify(notifications,never()).show(any());
    }
    @Test void unavailableChannelDoesNotClaimSuccess() throws Exception {
        doThrow(new IllegalStateException("DisabledForUser")).when(notifications).ready();
        request("/api/reminders",input(),503); assertThat(reminders.count()).isZero();
    }
    @Test void cancelBeforeDueSuppressesSend() throws Exception {
        var r=due(); var result=request("/api/reminders/"+r.id+"/cancel",Map.of(),200);
        assertThat(result.path("state").asText()).isEqualTo("CANCELLED");
        assertThat(result.path("confirmation").asText()).contains("到点不会再提醒");
        service.deliver(r.id); verify(notifications,never()).show(any());
    }
    @Test void sendsOnceAndDoesNotInventReceipt() throws Exception {
        var r=due(); service.deliver(r.id); service.deliver(r.id);
        verify(notifications,times(1)).show(any());
        var actual=reminders.findById(r.id).orElseThrow();
        assertThat(actual.state).isEqualTo(FIRED); assertThat(actual.receivedAt).isNull();
        assertThat(actual.firedAt).isNotNull();
        request("/api/reminders/"+r.id+"/events",Map.of("state","RECEIVED"),200);
        var cancelled=request("/api/reminders/"+r.id+"/cancel",Map.of(),200);
        assertThat(cancelled.path("confirmation").asText()).contains("此前发出的通知");
    }
    @Test void workerFailureAndCrashRecoveryNeverBlindlyRetry() throws Exception {
        var r=due(); doThrow(new IllegalStateException("OS timeout")).when(notifications).show(any());
        service.deliver(r.id); service.deliver(r.id);
        assertThat(reminders.findById(r.id).orElseThrow().state).isEqualTo(DELIVERY_FAILED);
        verify(notifications,times(1)).show(any());
        var orphan=due(); orphan.state=DISPATCHING; orphan.updatedAt=Instant.now().minusSeconds(120); reminders.saveAndFlush(orphan);
        service.scan(); assertThat(reminders.findById(orphan.id).orElseThrow().state).isEqualTo(DELIVERY_FAILED);
        verify(notifications,times(1)).show(any());
    }
    @Test void staleAndLegacyRowsAreNeverSentBySystemWorker() throws Exception {
        var stale=due(); stale.scheduledAt=Instant.now().minusSeconds(301); reminders.saveAndFlush(stale);
        var legacy=due(); legacy.channel=Reminder.Channel.CODEX; reminders.saveAndFlush(legacy);
        service.scan();
        assertThat(reminders.findById(stale.id).orElseThrow().state).isEqualTo(DELIVERY_FAILED);
        assertThat(reminders.findById(legacy.id).orElseThrow().state).isEqualTo(SCHEDULED);
        verify(notifications,never()).show(any());
    }
    @Test void cannotFakeSystemExecutionViaNativeEndpoints() throws Exception {
        var r=scheduled();
        request("/api/reminders/"+r.id+"/claim",Map.of(),409);
        request("/api/reminders/"+r.id+"/events",Map.of("state","RECEIVED"),409);
        request("/api/reminders/"+r.id+"/events",Map.of("state","FIRED"),409);
        assertThat(request("/api/reminders/"+r.id+"/dispatch",Map.of(),200).path("notify").asBoolean()).isFalse();
    }
    @Test void upgradesLegacyMysqlEnumWithoutLosingReminder() throws Exception {
        try(var connection=source.getConnection()) {
            Assumptions.assumeTrue(connection.getMetaData().getDatabaseProductName().equals("MySQL"));
        }
        var r=due();
        new org.springframework.jdbc.core.JdbcTemplate(source).execute("ALTER TABLE assistant_reminders MODIFY COLUMN state ENUM('PENDING','CREATING','SCHEDULED','PAUSED','FIRED','RECEIVED','FAILED','CANCEL_PENDING','CANCELLED') NOT NULL");
        schema.migrate(); schema.migrate(); service.deliver(r.id);
        assertThat(reminders.findById(r.id).orElseThrow().state).isEqualTo(FIRED);
        verify(notifications).show(any());
    }
    @Test void cancellationDuringSendWaitsForActualResult() throws Exception {
        var r=due(); var entered=new CountDownLatch(1); var release=new CountDownLatch(1);
        doAnswer(call->{ entered.countDown(); if(!release.await(3,TimeUnit.SECONDS)) throw new IllegalStateException("test timeout"); return null; }).when(notifications).show(any());
        try(var executor=Executors.newFixedThreadPool(2)) {
            var send=executor.submit(()->service.deliver(r.id));
            assertThat(entered.await(3,TimeUnit.SECONDS)).isTrue();
            var cancel=executor.submit(()->request("/api/reminders/"+r.id+"/cancel",Map.of(),200));
            release.countDown(); send.get(5,TimeUnit.SECONDS);
            assertThat(cancel.get(5,TimeUnit.SECONDS).path("confirmation").asText()).contains("此前发出的通知");
        } finally { release.countDown(); }
        assertThat(reminders.findById(r.id).orElseThrow().state).isEqualTo(CANCELLED);
    }
}
