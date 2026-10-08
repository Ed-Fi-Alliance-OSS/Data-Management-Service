// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

import java.net.ServerSocket;
import java.net.Socket;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.HashSet;
import java.util.Set;

/** Test-only TCP fault control. Inspects only the Kafka request API key; never decodes records.
 * Worker offsets/status/config bypass this listener. REST offset inspection may use it,
 * so metadata/fetch requests remain available while source Produce requests are disconnected.
 * Block confirmation is written only after all existing sockets have been closed. */
public class MessageContractProducerProxy {
    private static final Path BLOCK = Path.of("/tmp/contract-producer-block");
    private static final Path STATE = Path.of("/tmp/contract-producer-state");
    private static final Object LOCK = new Object();
    private static final Set<Socket> SOCKETS = new HashSet<>();
    private static boolean blocked;
    private static boolean priorBlocked;
    private static long rejected;
    private static long connected;

    public static void main(String[] args) throws Exception {
        try (var listener = new ServerSocket(19094)) {
            Thread.ofPlatform().daemon().start(() -> {
                try {
                    while (true) {
                        synchronized (LOCK) {
                            blocked = Files.exists(BLOCK);
                            if (blocked && !priorBlocked) {
                                for (Socket socket : SOCKETS) close(socket);
                                SOCKETS.clear();
                            }
                            priorBlocked = blocked;
                            Path temporary = STATE.resolveSibling("contract-producer-state.tmp");
                            Files.writeString(temporary, "{\"Blocked\":" + blocked
                                    + ",\"Rejected\":" + rejected + ",\"Connected\":" + connected + "}");
                            Files.move(temporary, STATE, StandardCopyOption.REPLACE_EXISTING,
                                    StandardCopyOption.ATOMIC_MOVE);
                        }
                        Thread.sleep(25);
                    }
                } catch (Exception failure) { System.exit(2); }
            });
            while (true) {
                Socket client = listener.accept();
                synchronized (LOCK) {
                    Socket upstream = new Socket();
                    try {
                        upstream.connect(new java.net.InetSocketAddress(args[0], 9094), 2000);
                    } catch (Exception failure) { close(client); close(upstream); continue; }
                    connected++;
                    SOCKETS.add(client);
                    SOCKETS.add(upstream);
                    Thread.ofPlatform().daemon().start(() -> requests(client, upstream));
                    Thread.ofPlatform().daemon().start(() -> copy(upstream, client));
                }
            }
        }
    }

    private static void requests(Socket source, Socket target) {
        try {
            var input = new java.io.DataInputStream(source.getInputStream());
            var output = new java.io.DataOutputStream(target.getOutputStream());
            byte[] buffer = new byte[8192];
            while (true) {
                int length = input.readInt();
                if (length < 2 || length > 140_000_000) break;
                short apiKey = input.readShort();
                synchronized (LOCK) {
                    // Produce is Kafka API key 0. Disconnect before any request bytes reach the broker.
                    if ((blocked || Files.exists(BLOCK)) && apiKey == 0) {
                        rejected++;
                        break;
                    }
                }
                output.writeInt(length);
                output.writeShort(apiKey);
                int remaining = length - 2;
                while (remaining > 0) {
                    int count = input.read(buffer, 0, Math.min(buffer.length, remaining));
                    if (count < 0) throw new java.io.EOFException();
                    output.write(buffer, 0, count);
                    remaining -= count;
                }
                output.flush();
            }
        } catch (Exception failure) { /* Expected for interrupted Produce requests. */ }
        finally {
            synchronized (LOCK) {
                close(source);
                close(target);
                SOCKETS.remove(source);
                SOCKETS.remove(target);
            }
        }
    }

    private static void copy(Socket source, Socket target) {
        try { source.getInputStream().transferTo(target.getOutputStream()); }
        catch (Exception failure) { /* Expected when the fault closes active connections. */ }
        finally {
            synchronized (LOCK) {
                close(source);
                close(target);
                SOCKETS.remove(source);
                SOCKETS.remove(target);
            }
        }
    }

    private static void close(Socket socket) {
        try { socket.close(); } catch (Exception failure) { /* Already closed. */ }
    }
}
