package io.ajmd.assistant;

import jakarta.servlet.*;
import jakarta.servlet.http.*;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Component;
import org.springframework.web.filter.OncePerRequestFilter;
import org.springframework.web.util.UrlPathHelper;

@Component
class ApiSecurity extends OncePerRequestFilter {
    private final byte[] expected;
    ApiSecurity(@Value("${assistant.token}") String token) {
        if (token.length() < 24) throw new IllegalStateException("Set ASSISTANT_API_TOKEN to a random token of at least 24 characters.");
        expected = ("Bearer " + token).getBytes(StandardCharsets.UTF_8);
    }
    @Override protected void doFilterInternal(HttpServletRequest req, HttpServletResponse res, FilterChain chain) throws ServletException, IOException {
        String path = UrlPathHelper.defaultInstance.getPathWithinApplication(req);
        // The release serves the compiled Vue app; only API data needs a bearer token.
        if (!(path.equals("/api") || path.startsWith("/api/")) || path.equals("/api/health")) {
            res.setHeader("Referrer-Policy", "no-referrer");
            chain.doFilter(req, res); return;
        }
        String header = req.getHeader("Authorization");
        if (header == null || !MessageDigest.isEqual(expected, header.getBytes(StandardCharsets.UTF_8))) {
            res.setStatus(401); res.setContentType("application/json;charset=UTF-8");
            res.getWriter().write("{\"message\":\"请先连接个人助手，访问密钥无效\"}"); return;
        }
        res.setHeader("Cache-Control", "no-store");
        chain.doFilter(req, res);
    }
}
