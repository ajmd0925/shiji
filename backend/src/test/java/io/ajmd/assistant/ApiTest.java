package io.ajmd.assistant;

import com.fasterxml.jackson.databind.*;
import java.time.Instant;
import java.util.*;
import org.junit.jupiter.api.*;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.AutoConfigureMockMvc;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.test.web.servlet.*;
import org.springframework.http.MediaType;
import static org.assertj.core.api.Assertions.*;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.*;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.*;

@SpringBootTest(properties={"assistant.reminders.channel=CODEX", "assistant.reminders.worker-enabled=false", "assistant.token=test-token-abcdefghijklmnopqrstuvwxyz", "spring.datasource.url=${TEST_DB_URL:jdbc:h2:mem:assistant-test;MODE=MySQL;DB_CLOSE_DELAY=-1}", "spring.datasource.username=${TEST_DB_USER:sa}", "spring.datasource.password=${TEST_DB_PASSWORD:}", "spring.jpa.hibernate.ddl-auto=create-drop"})
@AutoConfigureMockMvc
class ApiTest {
    @Autowired MockMvc mvc;
    @Autowired ObjectMapper json;
    @Autowired ReminderRepository reminders;
    @Autowired TaskRepository tasks;
    @Autowired NoteRepository notes;
    @Autowired SyncRepository sync;
    final String token="Bearer test-token-abcdefghijklmnopqrstuvwxyz";
    @BeforeEach void reset() { reminders.deleteAll(); tasks.deleteAll(); notes.deleteAll(); sync.deleteAll(); sync.save(new SyncState()); }
    JsonNode request(String method,String path,Object body,int status) throws Exception {
        var builder=switch(method) { case "POST"->post(path); case "PUT"->put(path); case "DELETE"->delete(path); default->get(path); };
        builder.header("Authorization",token).contentType(MediaType.APPLICATION_JSON);
        if(body!=null) builder.content(json.writeValueAsString(body));
        String result=mvc.perform(builder).andExpect(status().is(status)).andReturn().getResponse().getContentAsString();
        return result.isEmpty()?json.nullNode():json.readTree(result);
    }
    JsonNode task() throws Exception { return request("POST","/api/tasks",Map.of("title","喝水","description","每天照顾好自己","priority","NORMAL","completed",false),201); }
    Map<String,Object> reminderInput(String taskId) { return new HashMap<>(Map.of("taskId",taskId,"message","该喝水啦","scheduledAt",Instant.now().plusSeconds(180).toString(),"timezone","Asia/Shanghai","requestKey",UUID.randomUUID().toString())); }
    String reminder(String taskId) throws Exception { return request("POST","/api/reminders",reminderInput(taskId),201).get("id").asText(); }
    void claim(String id) throws Exception { request("POST","/api/reminders/"+id+"/claim",Map.of(),200); }

    @Test void healthPublicButPrivateDataRequiresToken() throws Exception {
        mvc.perform(get("/api/health")).andExpect(status().isOk());
        mvc.perform(get("/api/tasks")).andExpect(status().isUnauthorized());
        mvc.perform(post("/api/notes").contentType(MediaType.APPLICATION_JSON).content("{}")).andExpect(status().isUnauthorized());
        mvc.perform(post("/api/reminders/quick").contentType(MediaType.APPLICATION_JSON).content("{}")).andExpect(status().isUnauthorized());
        mvc.perform(get("/api/tasks").header("Authorization","Bearer bad")).andExpect(status().isUnauthorized());
        mvc.perform(get("/api")).andExpect(status().isUnauthorized());
        mvc.perform(get("/api/not-found")).andExpect(status().isUnauthorized());
        mvc.perform(get(java.net.URI.create("/%61pi/tasks"))).andExpect(status().isUnauthorized());
        mvc.perform(get("/launcher-test.txt")).andExpect(status().isOk())
            .andExpect(header().string("Referrer-Policy", "no-referrer"));
        mvc.perform(post("/api/desktop/shutdown")).andExpect(status().isUnauthorized());
        mvc.perform(post("/api/desktop/shutdown").header("Authorization", token)).andExpect(status().isNotFound());
    }
    @Test void taskCrudAndCompletion() throws Exception {
        String id=task().get("id").asText();
        assertThat(request("GET","/api/tasks",null,200).size()).isEqualTo(1);
        JsonNode changed=request("PUT","/api/tasks/"+id,Map.of("title","喝完水","description","","priority","HIGH","completed",true),200);
        assertThat(changed.get("completed").asBoolean()).isTrue();
        request("DELETE","/api/tasks/"+id,null,204);
        assertThat(request("GET","/api/tasks",null,200).isEmpty()).isTrue();
    }
    @Test void validationAndMissingResources() throws Exception {
        request("POST","/api/tasks",Map.of("title"," ","priority","NORMAL"),400);
        request("POST","/api/tasks",Map.of("title","x","priority","URGENT"),400);
        request("PUT","/api/tasks/"+UUID.randomUUID(),Map.of("title","x","priority","NORMAL"),404);
        request("GET","/api/reminders",null,200);
    }
    @Test void noteRoundTripPreservesTextAndPinnedOrder() throws Exception {
        var note=Map.of("title","灵感 <script>","content","第一行\n第二行 <b>原文</b>","color","SAGE","pinned",false);
        String id=request("POST","/api/notes",note,201).get("id").asText();
        request("POST","/api/notes",Map.of("title","置顶","content","","color","BLUE","pinned",true),201);
        JsonNode all=request("GET","/api/notes",null,200);
        assertThat(all.get(0).get("title").asText()).isEqualTo("置顶");
        assertThat(all.get(1).get("content").asText()).isEqualTo(note.get("content"));
        request("PUT","/api/notes/"+id,Map.of("title","已改","content","新内容","color","PEACH","pinned",false),200);
        request("DELETE","/api/notes/"+id,null,204);
    }
    @Test void reminderIdempotencyAndDuplicateGuard() throws Exception {
        String taskId=task().get("id").asText(); var input=reminderInput(taskId);
        var first=request("POST","/api/reminders",input,201);
        assertThat(first.get("state").asText()).isEqualTo("PENDING");
        assertThat(request("POST","/api/reminders",input,201).get("id")).isEqualTo(first.get("id"));
        request("POST","/api/reminders",reminderInput(taskId),409);
        input.put("message","other"); request("POST","/api/reminders",input,409);
        assertThat(reminders.count()).isEqualTo(1);
    }
    @Test void longUnicodeNotePersists() throws Exception {
        String content="记".repeat(20000);
        String id=request("POST","/api/notes",Map.of("title","长文本","content",content,"color","LAVENDER","pinned",false),201).get("id").asText();
        assertThat(notes.findById(UUID.fromString(id)).orElseThrow().content).isEqualTo(content);
    }
    @Test void stateMachineRequiresActualAutomationIdAndUserReceipt() throws Exception {
        String id=reminder(task().get("id").asText()); String path="/api/reminders/"+id+"/events";
        request("POST",path,Map.of("state","FIRED"),409);
        request("POST",path,Map.of("state","RECEIVED"),409);
        request("POST",path,Map.of("state","SCHEDULED"),400);
        request("POST",path,Map.of("state","SCHEDULED","automationId","native-1"),409);
        claim(id);
        request("POST","/api/reminders/"+id+"/claim",Map.of(),409);
        request("POST",path,Map.of("state","SCHEDULED","automationId","native-1"),200);
        request("POST",path,Map.of("state","SCHEDULED","automationId","native-1"),200);
        request("POST",path,Map.of("state","SCHEDULED","automationId","native-2"),409);
        request("POST",path,Map.of("state","FIRED"),200);
        assertThat(reminders.findById(UUID.fromString(id)).orElseThrow().receivedAt).isNull();
        request("POST",path,Map.of("state","RECEIVED"),200);
        assertThat(reminders.findById(UUID.fromString(id)).orElseThrow().receivedAt).isNotNull();
    }
    @Test void scheduledReminderCancellationMustBeConfirmed() throws Exception {
        String taskId=task().get("id").asText(); String id=reminder(taskId); String path="/api/reminders/"+id;
        claim(id);
        request("POST",path+"/events",Map.of("state","SCHEDULED","automationId","native-test"),200);
        request("DELETE","/api/tasks/"+taskId,null,409);
        assertThat(request("POST",path+"/cancel",Map.of(),200).get("state").asText()).isEqualTo("CANCEL_PENDING");
        request("DELETE","/api/tasks/"+taskId,null,409);
        request("POST",path+"/events",Map.of("state","CANCELLED"),200);
        request("DELETE","/api/tasks/"+taskId,null,204);
    }
    @Test void pendingReminderCanBeCancelledWithoutNativeTool() throws Exception {
        String id=reminder(task().get("id").asText());
        assertThat(request("POST","/api/reminders/"+id+"/cancel",Map.of(),200).get("state").asText()).isEqualTo("CANCELLED");
        request("POST","/api/reminders/"+id+"/events",Map.of("state","SCHEDULED","automationId","x"),409);
    }
    @Test void receivedReminderMustBeCancelledBeforeDeletingTask() throws Exception {
        String taskId=task().get("id").asText(); String id=reminder(taskId); claim(id);
        request("POST","/api/reminders/"+id+"/events",Map.of("state","SCHEDULED","automationId","test-native"),200);
        request("POST","/api/reminders/"+id+"/events",Map.of("state","RECEIVED"),200);
        request("DELETE","/api/tasks/"+taskId,null,409);
        request("POST","/api/reminders/"+id+"/cancel",Map.of(),200);
        request("POST","/api/reminders/"+id+"/events",Map.of("state","CANCELLED"),200);
        request("DELETE","/api/tasks/"+taskId,null,204);
    }
    @Test void rejectsPastTimesBadTimezoneAndCompletedTask() throws Exception {
        String taskId=task().get("id").asText(); var input=reminderInput(taskId);
        input.put("scheduledAt",Instant.now().minusSeconds(60).toString()); request("POST","/api/reminders",input,400);
        input.put("scheduledAt",Instant.now().plusSeconds(180).toString()); input.put("timezone","Invalid/Zone"); request("POST","/api/reminders",input,400);
        input.put("timezone","Asia/Shanghai");
        request("PUT","/api/tasks/"+taskId,Map.of("title","喝水","priority","NORMAL","completed",true),200);
        request("POST","/api/reminders",input,409);
    }
    @Test void dispatchChecksCancellationAndOnlyClaimsOnce() throws Exception {
        String id=reminder(task().get("id").asText()); claim(id);
        request("POST","/api/reminders/"+id+"/events",Map.of("state","SCHEDULED","automationId","native-guard"),200);
        assertThat(request("POST","/api/reminders/"+id+"/dispatch",Map.of(),200).get("notify").asBoolean()).isFalse();
        Reminder r=reminders.findById(UUID.fromString(id)).orElseThrow(); r.scheduledAt=Instant.now().minusSeconds(1); reminders.saveAndFlush(r);
        assertThat(request("POST","/api/reminders/"+id+"/dispatch",Map.of(),200).get("notify").asBoolean()).isTrue();
        assertThat(request("POST","/api/reminders/"+id+"/dispatch",Map.of(),200).get("notify").asBoolean()).isFalse();
        assertThat(reminders.findById(UUID.fromString(id)).orElseThrow().receivedAt).isNull();
        request("POST","/api/reminders/"+id+"/cancel",Map.of(),200);
        assertThat(request("POST","/api/reminders/"+id+"/dispatch",Map.of(),200).get("notify").asBoolean()).isFalse();
    }
    @Test void cancellationDuringCreationStaysPendingAfterBinding() throws Exception {
        String id=reminder(task().get("id").asText()); claim(id);
        request("POST","/api/reminders/"+id+"/cancel",Map.of(),200);
        JsonNode bound=request("POST","/api/reminders/"+id+"/events",Map.of("state","SCHEDULED","automationId","real-result-after-cancel"),200);
        assertThat(bound.get("state").asText()).isEqualTo("CANCEL_PENDING");
        assertThat(bound.get("automationId").asText()).isEqualTo("real-result-after-cancel");
        assertThat(request("POST","/api/reminders/"+id+"/dispatch",Map.of(),200).get("notify").asBoolean()).isFalse();
        request("POST","/api/reminders/"+id+"/events",Map.of("state","CANCELLED"),200);
    }
    @Test void cancelBeforeDispatchSuppressesNotificationAndPausedIsAccurate() throws Exception {
        String id=reminder(task().get("id").asText()); claim(id);
        request("POST","/api/reminders/"+id+"/events",Map.of("state","PAUSED","automationId","native-paused","detail","实际时间不符，保留记录"),200);
        assertThat(request("POST","/api/reminders/"+id+"/dispatch",Map.of(),200).get("notify").asBoolean()).isFalse();
        request("POST","/api/reminders/"+id+"/cancel",Map.of(),200);
        assertThat(request("POST","/api/reminders/"+id+"/dispatch",Map.of(),200).get("notify").asBoolean()).isFalse();
    }
    @Test void syncLeaseAndCompletionCannotBeFakedByListOrOverlap() throws Exception {
        reminder(task().get("id").asText());
        assertThat(request("GET","/api/sync/status",null,200).get("recent").asBoolean()).isFalse();
        request("POST","/api/sync/check",Map.of("automationId","sync-real"),409);
        request("POST","/api/sync/config",Map.of("enabled",true,"automationId","sync-real"),200);
        JsonNode check=request("POST","/api/sync/check",Map.of("automationId","sync-real"),200);
        assertThat(check.get("acquired").asBoolean()).isTrue(); assertThat(check.get("creations").size()).isEqualTo(1);
        assertThat(request("POST","/api/sync/check",Map.of("automationId","sync-real"),200).get("acquired").asBoolean()).isFalse();
        request("POST","/api/sync/complete",Map.of("runId","wrong-run"),409);
        assertThat(request("POST","/api/sync/complete",Map.of("runId",check.get("runId").asText()),200).get("recent").asBoolean()).isTrue();
        JsonNode second=request("POST","/api/sync/check",Map.of("automationId","sync-real"),200);
        assertThat(second.get("acquired").asBoolean()).isTrue();
        JsonNode failed=request("POST","/api/sync/complete",Map.of("runId",second.get("runId").asText(),"error","宿主拒绝取消，等待重试"),200);
        assertThat(failed.get("recent").asBoolean()).isFalse();
        assertThat(failed.get("error").asText()).contains("等待重试");
    }
}
