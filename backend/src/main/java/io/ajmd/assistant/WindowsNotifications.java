package io.ajmd.assistant;

import com.fasterxml.jackson.databind.*;
import jakarta.annotation.PreDestroy;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import org.springframework.stereotype.Component;

/** A persistent, hidden Windows PowerShell bridge. JSON data is never executable source. */
@Component
class WindowsNotifications {
    private final ObjectMapper json;
    private Process process;
    private BufferedWriter input;
    private BlockingQueue<String> responses;
    WindowsNotifications(ObjectMapper json) { this.json=json; }

    synchronized void ready() { exchange(Map.of("command","ping")); }
    synchronized void show(Reminder reminder) {
        exchange(Map.of("command","show","id",reminder.id.toString(),"title",reminder.title,"message",reminder.message));
    }
    private JsonNode exchange(Map<String,String> request) {
        try {
            if(process==null || !process.isAlive()) start();
            input.write(json.writeValueAsString(request)); input.newLine(); input.flush();
            String line=responses.poll(5,TimeUnit.SECONDS);
            if(line==null || line.equals("EOF")) throw new IOException("Windows 通知服务未响应，结果待核对");
            JsonNode result=json.readTree(line);
            if(!result.path("ok").asBoolean()) throw new IOException(result.path("error").asText("Windows 通知接口拒绝请求"));
            return result;
        } catch(Exception e) {
            if(e instanceof InterruptedException) Thread.currentThread().interrupt();
            close();
            throw new IllegalStateException(Objects.requireNonNullElse(e.getMessage(),"Windows 通知服务不可用"),e);
        }
    }
    private void start() throws IOException {
        if(!System.getProperty("os.name").startsWith("Windows")) throw new IOException("当前系统不支持 Windows 通知");
        String source;
        try(var resource=getClass().getResourceAsStream("/windows-notifications.ps1")) {
            if(resource==null) throw new IOException("缺少通知桥接程序");
            source=new String(resource.readAllBytes(),StandardCharsets.UTF_8);
        }
        String encoded=Base64.getEncoder().encodeToString(source.getBytes(StandardCharsets.UTF_16LE));
        String executable=System.getenv("SystemRoot")+"\\System32\\WindowsPowerShell\\v1.0\\powershell.exe";
        process=new ProcessBuilder(executable,"-NoLogo","-NoProfile","-NonInteractive","-WindowStyle","Hidden","-EncodedCommand",encoded)
            .redirectError(ProcessBuilder.Redirect.DISCARD).start();
        input=process.outputWriter(StandardCharsets.UTF_8);
        var queue=new LinkedBlockingQueue<String>(4); responses=queue;
        var output=process.inputReader(StandardCharsets.UTF_8);
        Thread.ofPlatform().daemon().name("shiji-notification-output").start(()->{
            try(output) { String line; while((line=output.readLine())!=null) if(!queue.offer(line)) break; }
            catch(IOException ignored) { }
            finally { queue.offer("EOF"); }
        });
    }
    @PreDestroy synchronized void close() {
        if(process!=null) process.destroyForcibly(); process=null; input=null;
    }
}
