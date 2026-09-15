#include <wayland-client.h>
#include "data-control-client.h"
#include <algorithm>
#include <cerrno>
#include <chrono>
#include <csignal>
#include <cstdint>
#include <fcntl.h>
#include <iostream>
#include <memory>
#include <poll.h>
#include <string>
#include <string_view>
#include <unistd.h>
#include <vector>

namespace {
constexpr std::size_t text_limit = 1024 * 1024;
constexpr std::size_t file_list_limit = 16 * 1024 * 1024;
const std::string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
std::string encode(std::string_view input) {
    std::string result;
    std::uint32_t value = 0;
    int bits = -6;
    for (unsigned char c : input) {
        value = (value << 8) | c; bits += 8;
        while (bits >= 0) { result += alphabet[(value >> bits) & 63]; bits -= 6; }
    }
    if (bits > -6) result += alphabet[((value << 8) >> (bits + 8)) & 63];
    while (result.size() % 4) result += '=';
    return result;
}
bool decode(std::string_view input, std::string &result) {
    if (input.size() % 4) return false;
    result.clear();
    std::uint32_t value = 0;
    int bits = -8;
    bool padding = false;
    for (char c : input) {
        if (c == '=') { padding = true; continue; }
        auto n = alphabet.find(c);
        if (padding || n == std::string::npos) return false;
        value = (value << 6) | static_cast<unsigned>(n); bits += 6;
        if (bits >= 0) { result += static_cast<char>((value >> bits) & 255); bits -= 8; }
    }
    return encode(result) == input;
}
void emit(std::string_view type, std::string_view value = {}) {
    std::cout << type;
    if (!value.empty()) std::cout << '\t' << value;
    std::cout << '\n' << std::flush;
}
struct Offer { zwlr_data_control_offer_v1 *object{}; std::vector<std::string> mimes; };
struct Source {
    zwlr_data_control_source_v1 *object{};
    std::shared_ptr<std::string> data;
    std::shared_ptr<std::string> gnome;
    bool files{};
    bool cancelled{};
};
using Clock = std::chrono::steady_clock;
struct Write { int fd{-1}; std::shared_ptr<std::string> data; std::size_t offset{}; Clock::time_point deadline; };
struct Sync { wl_callback *callback{}; std::string id; };
wl_display *display{};
wl_seat *seat{};
zwlr_data_control_manager_v1 *manager{};
zwlr_data_control_device_v1 *device{};
bool armed = false;
bool alive = true;
std::vector<std::unique_ptr<Offer>> offers;
std::vector<std::unique_ptr<Source>> sources;
std::vector<Write> writes;
int read_fd = -1;
bool read_files = false;
std::string read_data;
Clock::time_point read_deadline;

void offer_mime(void *data, zwlr_data_control_offer_v1 *, const char *mime) {
    static_cast<Offer *>(data)->mimes.emplace_back(mime);
}
const zwlr_data_control_offer_v1_listener offer_listener{offer_mime};
void new_offer(void *, zwlr_data_control_device_v1 *, zwlr_data_control_offer_v1 *object) {
    auto offer = std::make_unique<Offer>(); offer->object = object;
    zwlr_data_control_offer_v1_add_listener(object, &offer_listener, offer.get());
    offers.emplace_back(std::move(offer));
}
void selected(void *, zwlr_data_control_device_v1 *, zwlr_data_control_offer_v1 *object) {
    if (read_fd >= 0) { close(read_fd); read_fd = -1; }
    read_data.clear();
    if (armed) emit("CHANGED");
    if (armed && object) {
        auto *offer = static_cast<Offer *>(zwlr_data_control_offer_v1_get_user_data(object));
        const std::vector<std::string> preferences{"x-special/gnome-copied-files", "text/uri-list", "text/plain;charset=utf-8", "UTF8_STRING", "text/plain"};
        for (const auto &mime : preferences) {
            if (std::find(offer->mimes.begin(), offer->mimes.end(), mime) == offer->mimes.end()) continue;
            int pipes[2];
            if (pipe2(pipes, O_CLOEXEC) != 0) break;
            fcntl(pipes[0], F_SETFL, O_NONBLOCK);
            read_fd = pipes[0]; read_files = mime == preferences[0] || mime == preferences[1];
            read_deadline = Clock::now() + std::chrono::seconds(30);
            zwlr_data_control_offer_v1_receive(object, mime.c_str(), pipes[1]);
            close(pipes[1]); wl_display_flush(display); break;
        }
    }
    // receive() transfers data through the pipe; the offer itself is no longer needed.
    for (auto &offer : offers) zwlr_data_control_offer_v1_destroy(offer->object);
    offers.clear();
}
void finished(void *, zwlr_data_control_device_v1 *) { alive = false; }
void primary(void *, zwlr_data_control_device_v1 *, zwlr_data_control_offer_v1 *) {}
const zwlr_data_control_device_v1_listener device_listener{new_offer, selected, finished, primary};
void source_send(void *data, zwlr_data_control_source_v1 *, const char *mime, int fd) {
    auto *source = static_cast<Source *>(data);
    if (writes.size() >= 16) { close(fd); return; }
    auto content = source->data;
    if (std::string_view(mime) == "x-special/gnome-copied-files") content = source->gnome;
    if (!content) { close(fd); return; }
    fcntl(fd, F_SETFL, O_NONBLOCK);
    writes.push_back(Write{fd, content, 0, Clock::now() + std::chrono::seconds(30)});
}
void source_cancelled(void *data, zwlr_data_control_source_v1 *) { static_cast<Source *>(data)->cancelled = true; }
const zwlr_data_control_source_v1_listener source_listener{source_send, source_cancelled};
void sync_done(void *data, wl_callback *callback, std::uint32_t) {
    std::unique_ptr<Sync> sync(static_cast<Sync *>(data));
    wl_callback_destroy(callback); emit("ACK", sync->id);
}
const wl_callback_listener sync_listener{sync_done};
bool publish(std::string_view line) {
    auto a = line.find('\t'); auto b = line.find('\t', a == std::string_view::npos ? 0 : a + 1);
    if (a == std::string_view::npos || b == std::string_view::npos) return false;
    auto kind = line.substr(0, a); auto id = line.substr(a + 1, b - a - 1);
    if ((kind != "TEXT" && kind != "FILES") || id.empty() || id.size() > 128 ||
        id.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-") != std::string_view::npos) return false;
    std::string content;
    if (!decode(line.substr(b + 1), content) || content.size() > (kind == "TEXT" ? text_limit : file_list_limit)) return false;
    auto source = std::make_unique<Source>();
    source->object = zwlr_data_control_manager_v1_create_data_source(manager);
    source->files = kind == "FILES"; source->data = std::make_shared<std::string>(std::move(content));
    zwlr_data_control_source_v1_add_listener(source->object, &source_listener, source.get());
    if (source->files) {
        source->gnome = std::make_shared<std::string>("copy\n");
        for (char c : *source->data) if (c != '\r') *source->gnome += c;
        zwlr_data_control_source_v1_offer(source->object, "text/uri-list");
        zwlr_data_control_source_v1_offer(source->object, "x-special/gnome-copied-files");
    } else {
        zwlr_data_control_source_v1_offer(source->object, "text/plain;charset=utf-8");
        zwlr_data_control_source_v1_offer(source->object, "text/plain");
        zwlr_data_control_source_v1_offer(source->object, "UTF8_STRING");
    }
    zwlr_data_control_device_v1_set_selection(device, source->object);
    sources.emplace_back(std::move(source));
    auto *sync = new Sync{wl_display_sync(display), std::string(id)};
    wl_callback_add_listener(sync->callback, &sync_listener, sync);
    wl_display_flush(display); return true;
}
void global(void *, wl_registry *registry, std::uint32_t name, const char *interface, std::uint32_t version) {
    if (!manager && std::string_view(interface) == zwlr_data_control_manager_v1_interface.name)
        manager = static_cast<zwlr_data_control_manager_v1 *>(wl_registry_bind(registry, name, &zwlr_data_control_manager_v1_interface, std::min(version, 2u)));
    if (!seat && std::string_view(interface) == wl_seat_interface.name)
        seat = static_cast<wl_seat *>(wl_registry_bind(registry, name, &wl_seat_interface, std::min(version, 7u)));
}
void removed(void *, wl_registry *, std::uint32_t) {}
const wl_registry_listener registry_listener{global, removed};
int run() {
    display = wl_display_connect(nullptr);
    if (!display) return 1;
    auto *registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &registry_listener, nullptr);
    if (wl_display_roundtrip(display) < 0 || !manager || !seat) return 1;
    device = zwlr_data_control_manager_v1_get_data_device(manager, seat);
    zwlr_data_control_device_v1_add_listener(device, &device_listener, nullptr);
    if (wl_display_roundtrip(display) < 0 || wl_display_roundtrip(display) < 0) return 1;
    armed = true; emit("READY");
    std::string input;
    while (alive) {
        if (wl_display_dispatch_pending(display) < 0) break;
        if (wl_display_flush(display) < 0 && errno != EAGAIN) break;
        std::vector<pollfd> fds{{wl_display_get_fd(display), POLLIN, 0}, {STDIN_FILENO, POLLIN, 0}};
        const int current_read = read_fd;
        if (current_read >= 0) fds.push_back({current_read, POLLIN, 0});
        for (const auto &write : writes) fds.push_back({write.fd, POLLOUT, 0});
        if (poll(fds.data(), fds.size(), 1000) < 0) { if (errno == EINTR) continue; break; }
        if (read_fd >= 0 && Clock::now() > read_deadline) { close(read_fd); read_fd = -1; }
        if (fds[0].revents & (POLLERR | POLLHUP)) break;
        if ((fds[0].revents & POLLIN) && wl_display_dispatch(display) < 0) break;
        if (fds[1].revents & (POLLIN | POLLHUP | POLLERR)) {
            char buffer[65536]; auto n = read(STDIN_FILENO, buffer, sizeof(buffer));
            if (n <= 0) break;
            input.append(buffer, n);
            if (input.size() > file_list_limit * 2) break;
            std::size_t end;
            while ((end = input.find('\n')) != std::string::npos) {
                if (!publish(std::string_view(input).substr(0, end))) { emit("ERROR", "invalid-publish"); alive = false; break; }
                input.erase(0, end + 1);
            }
        }
        if (read_fd >= 0 && read_fd == current_read && fds[2].revents) {
            char buffer[65536]; ssize_t n;
            while ((n = read(read_fd, buffer, sizeof(buffer))) > 0) {
                read_data.append(buffer, n);
                if (read_data.size() > (read_files ? file_list_limit : text_limit)) {
                    close(read_fd); read_fd = -1; emit("ERROR", "clipboard-too-large"); break;
                }
            }
            if (read_fd >= 0 && n == 0) { emit(read_files ? "FILES" : "TEXT", encode(read_data)); close(read_fd); read_fd = -1; }
        }
        for (auto &write : writes) {
            if (Clock::now() > write.deadline) { close(write.fd); write.fd = -1; continue; }
            auto ready = std::find_if(fds.begin(), fds.end(), [&](const pollfd &fd) { return fd.fd == write.fd && fd.revents; });
            if (ready == fds.end()) continue;
            auto n = ::write(write.fd, write.data->data() + write.offset, write.data->size() - write.offset);
            if (n >= 0) write.offset += n;
            if (write.offset == write.data->size() || (n < 0 && errno != EAGAIN && errno != EINTR)) { close(write.fd); write.fd = -1; }
        }
        std::erase_if(writes, [](const Write &write) { return write.fd < 0; });
        std::erase_if(sources, [](const auto &source) {
            if (!source->cancelled) return false;
            zwlr_data_control_source_v1_destroy(source->object); return true;
        });
    }
    if (read_fd >= 0) close(read_fd);
    for (const auto &write : writes) close(write.fd);
    for (const auto &source : sources) zwlr_data_control_source_v1_destroy(source->object);
    for (const auto &offer : offers) zwlr_data_control_offer_v1_destroy(offer->object);
    zwlr_data_control_device_v1_destroy(device); zwlr_data_control_manager_v1_destroy(manager);
    wl_seat_destroy(seat); wl_registry_destroy(registry); wl_display_disconnect(display);
    return 0;
}
}
int main(int argc, char **argv) {
    std::signal(SIGPIPE, SIG_IGN);
    if (argc == 2 && std::string_view(argv[1]) == "--self-test") {
        std::string decoded;
        for (const std::string &input : std::vector<std::string>{"", "f", "fo", "foo", "f\n\t", std::string("a\0b", 3)})
            if (!decode(encode(input), decoded) || decoded != input) return 1;
        if (decode("===x", decoded) || decode("????", decoded) || decode("Zg=", decoded)) return 1;
        std::cout << "Native clipboard checks passed.\n"; return 0;
    }
    if (argc != 1 || geteuid() == 0) return 2;
    return run();
}
