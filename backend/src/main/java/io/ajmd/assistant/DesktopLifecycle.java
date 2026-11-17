package io.ajmd.assistant;

import java.util.Map;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.context.ConfigurableApplicationContext;
import org.springframework.http.HttpStatus;
import org.springframework.web.bind.annotation.*;

/** Opt-in authenticated shutdown lets the portable launcher close the database cleanly. */
@RestController
@RequestMapping("/api/desktop")
@ConditionalOnProperty(name="assistant.desktop.enabled", havingValue="true")
class DesktopLifecycle {
    private final ConfigurableApplicationContext context;
    DesktopLifecycle(ConfigurableApplicationContext context) { this.context = context; }

    @GetMapping("/status") Map<String, String> status() { return Map.of("application", "shiji"); }

    @PostMapping("/shutdown")
    @ResponseStatus(HttpStatus.ACCEPTED)
    Map<String, String> shutdown() {
        new Thread(() -> {
            try { Thread.sleep(300); }
            catch (InterruptedException e) { Thread.currentThread().interrupt(); return; }
            context.close();
        }, "shiji-desktop-shutdown").start();
        return Map.of("status", "stopping");
    }
}
