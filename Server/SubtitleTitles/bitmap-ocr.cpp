#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_set>
#include <fcntl.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>
#include <vector>
#include <tesseract/baseapi.h>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/crc.h>
#include <libavutil/intreadwrite.h>
#include <libavutil/mem.h>
#include <libbluray/bluray.h>
#include <libbluray/filesystem.h>
#include <strings.h>
}

namespace {
using Clock = std::chrono::steady_clock;
constexpr int64_t MiB = 1024 * 1024;
constexpr double fractions[] = {.15, .35, .55, .75, .90};
constexpr double window_seconds = 10;
constexpr size_t region_count = std::size(fractions);
constexpr int64_t window_bytes = 192 * MiB;
constexpr int64_t track_bytes = 984 * MiB;
constexpr int64_t cpu_budget_ms = 16000; // Stop cleanly before the 18 s kernel backstop.
constexpr double preroll_seconds = 4;
constexpr int max_width = 4096, max_height = 2160;

struct Limits {
    std::string main;
    std::string companion;
    int64_t remaining = 24 * MiB;
    int64_t total = track_bytes;
    int64_t charged = 0;
    Clock::time_point started = Clock::now();
    bool exhausted = false;

    static int64_t cpu_ms() {
        timespec t{};
        clock_gettime(CLOCK_PROCESS_CPUTIME_ID, &t);
        return t.tv_sec * 1000 + t.tv_nsec / 1000000;
    }

    // Give the NAS idle time between work bursts. RLIMIT_CPU separately bounds total CPU time.
    void pace() {
        const auto cpu = cpu_ms();
        if (cpu - charged < 20) return;
        charged = cpu;
        std::this_thread::sleep_until(started + std::chrono::milliseconds(cpu * 4));
    }
};

constexpr int64_t sample_bytes = 32 * MiB;
constexpr size_t sample_reserve = 4 * MiB;
constexpr std::array<uint8_t, 5> sample_magic{'B', 'O', 'C', 'R', 1};
constexpr std::array<uint8_t, 8> sample_end{'B', 'O', 'C', 'R', 'E', 'N', 'D', 1};
// Each record has LE16 width and height, one region byte, LE32 CRC, then width*height pixels.
using SampleHeader = std::array<uint8_t, 9>;

uint32_t sample_crc(const SampleHeader& header, const uint8_t* pixels, size_t count) {
    const auto* table = av_crc_get_table(AV_CRC_32_IEEE_LE);
    return av_crc(table, av_crc(table, 0, header.data(), 5), pixels, count);
}

class SampleWriter {
public:
    explicit SampleWriter(const char* path) : path(path) {
        if (!path) return;
        const int fd = open(path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
        if (fd < 0) return;
        file = fdopen(fd, "wb");
        if (!file) {
            close(fd);
            std::remove(path);
        } else if (!write(sample_magic.data(), sample_magic.size())) discard();
    }

    ~SampleWriter() { if (file) discard(); }
    SampleWriter(const SampleWriter&) = delete;
    SampleWriter& operator=(const SampleWriter&) = delete;

    void append(const uint8_t* pixels, int w, int h, size_t region) {
        if (!file) return;
        SampleHeader header;
        const size_t count = static_cast<size_t>(w) * h;
        const size_t bytes = count + header.size();
        // Keep space for later regions even if a dense early region uses the shared pool.
        const size_t local = std::min(bytes, sample_reserve - used[region]);
        const size_t shared = bytes - local;
        if (shared > shared_remaining) return;
        AV_WL16(header.data(), w);
        AV_WL16(header.data() + 2, h);
        header[4] = static_cast<uint8_t>(region);
        AV_WL32(header.data() + 5, sample_crc(header, pixels, count));
        if (!write(header.data(), header.size()) || !write(pixels, count)) {
            discard();
            return;
        }
        used[region] += local;
        shared_remaining -= shared;
    }

    void finish() {
        if (!file) return;
        const bool complete = write(sample_end.data(), sample_end.size());
        if (std::fclose(file) != 0 || !complete) std::remove(path);
        file = nullptr;
    }

private:
    bool write(const uint8_t* data, size_t bytes) {
        return std::fwrite(data, 1, bytes, file) == bytes && !std::ferror(file);
    }

    void discard() {
        std::fclose(file);
        file = nullptr;
        std::remove(path);
    }

    const char* path;
    FILE* file = nullptr;
    std::array<size_t, region_count> used{};
    size_t shared_remaining = sample_bytes - region_count * sample_reserve - sample_magic.size() - sample_end.size();
};

struct Reader { FILE* file; Limits* limits; };
int read_packet(void* opaque, uint8_t* output, int count) {
    auto& r = *static_cast<Reader*>(opaque);
    auto& l = *r.limits;
    l.pace();
    if (l.remaining <= 0 || l.total <= 0) { l.exhausted = true; return AVERROR(EIO); }
    const auto n = std::fread(output, 1, static_cast<size_t>(std::min<int64_t>({count, l.remaining, l.total})), r.file);
    l.remaining -= n;
    l.total -= n;
    return n ? static_cast<int>(n) : std::ferror(r.file) ? AVERROR(EIO) : AVERROR_EOF;
}

int64_t seek_packet(void* opaque, int64_t offset, int whence) {
    auto* f = static_cast<Reader*>(opaque)->file;
    whence &= ~AVSEEK_FORCE;
    if (whence == AVSEEK_SIZE) {
        auto current = ftello(f);
        if (current < 0 || fseeko(f, 0, SEEK_END) != 0) return AVERROR(EIO);
        auto size = ftello(f);
        return fseeko(f, current, SEEK_SET) == 0 ? size : AVERROR(EIO);
    }
    return fseeko(f, offset, whence) == 0 ? ftello(f) : AVERROR(EIO);
}

int open_file_io(Limits& limits, AVIOContext** out, FILE* file) {
    if (!file) return AVERROR(errno);
    if (std::setvbuf(file, nullptr, _IONBF, 0) != 0) {
        std::fclose(file);
        return AVERROR(EIO);
    }
    auto state = std::make_unique<Reader>(Reader{file, &limits});
    auto* buffer = static_cast<uint8_t*>(av_malloc(32768));
    if (buffer) *out = avio_alloc_context(buffer, 32768, 0, state.get(), read_packet, nullptr, seek_packet);
    if (!buffer || !*out) { av_free(buffer); std::fclose(file); return AVERROR(ENOMEM); }
    state.release();
    return 0;
}
int open_io(Limits& limits, AVIOContext** out, const char* url) {
    return open_file_io(limits, out, std::fopen(url, "rb"));
}

void close_io(AVIOContext* io) {
    auto* r = static_cast<Reader*>(io->opaque);
    std::fclose(r->file);
    delete r;
    av_freep(&io->buffer);
    avio_context_free(&io);
}

int open_companion(AVFormatContext* fmt, AVIOContext** out, const char* url, int flags, AVDictionary**) {
    auto& limits = *static_cast<Limits*>(fmt->opaque);
    if (flags != AVIO_FLAG_READ || limits.companion != url) return AVERROR(EINVAL);
    const int fd = open(url, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) return AVERROR(errno);
    struct stat info{};
    if (fstat(fd, &info) != 0 || !S_ISREG(info.st_mode) || info.st_size > 24 * MiB) {
        close(fd);
        return AVERROR(EINVAL);
    }
    auto* file = fdopen(fd, "rb");
    if (!file) {
        close(fd);
        return AVERROR(EIO);
    }
    return open_file_io(limits, out, file);
}

int close_companion(AVFormatContext*, AVIOContext* io) {
    close_io(io);
    return 0;
}

using Input = std::unique_ptr<AVFormatContext, void(*)(AVFormatContext*)>;
void close_input(AVFormatContext* fmt) { avformat_close_input(&fmt); }

constexpr uint64_t bluray_clock = 90000;
constexpr uint64_t m2ts_packet_bytes = 192;
struct BlurayClip {
    uint64_t first_byte, end_byte, start, end, in;
    bool has_pid;

    double time(int64_t pts, AVRational base) const {
        // MPEG-TS is a 33-bit clock; unwrap around this play item's local interval.
        constexpr uint64_t wrap = uint64_t{1} << 33;
        const auto middle = (end - start) / 2;
        const auto ticks = static_cast<uint64_t>(av_rescale_q(pts, base, {1, bluray_clock}));
        const auto local = static_cast<int64_t>((ticks - in - middle + wrap / 2) & (wrap - 1))
            - static_cast<int64_t>(wrap / 2);
        return (static_cast<int64_t>(start + middle) + local) / double(bluray_clock);
    }
};

struct BlurayReader {
    Limits& limits;
    std::unique_ptr<FILE, decltype(&std::fclose)> file{nullptr, std::fclose};
    std::unique_ptr<BLURAY, decltype(&bd_close)> disc{nullptr, bd_close};
    std::vector<BlurayClip> clips{};
};

int read_disc_blocks(void* opaque, void* buffer, int lba, int count) {
    auto& r = *static_cast<BlurayReader*>(opaque);
    constexpr int sector = 2048;
    if (lba < 0 || count <= 0) return -1;
    auto& limits = r.limits;
    limits.pace();
    const auto blocks = std::min<int64_t>({count, limits.remaining / sector, limits.total / sector});
    if (blocks <= 0) { limits.exhausted = true; return -1; }
    if (fseeko(r.file.get(), static_cast<int64_t>(lba) * sector, SEEK_SET) != 0) return -1;
    const auto read = std::fread(buffer, sector, static_cast<size_t>(blocks), r.file.get());
    limits.remaining -= read * sector;
    limits.total -= read * sector;
    return read ? static_cast<int>(read) : -1;
}

struct BluraySlice {
    BlurayReader& reader;
    uint64_t end_byte;
};

int read_bluray(void* opaque, uint8_t* output, int count) {
    auto& slice = *static_cast<BluraySlice*>(opaque);
    auto& r = slice.reader;
    if (r.limits.exhausted) return AVERROR(EIO);
    const auto position = bd_tell(r.disc.get());
    if (position >= slice.end_byte) return AVERROR_EOF;
    const auto n = bd_read(r.disc.get(), output, std::min<uint64_t>(count, slice.end_byte - position));
    return n > 0 ? n : AVERROR(EIO);
}

void close_bluray_io(AVIOContext* io) {
    av_freep(&io->buffer);
    avio_context_free(&io);
}

Input open_bluray_input(BluraySlice& slice, AVIOContext** io) {
    auto* buffer = static_cast<uint8_t*>(av_malloc(32768));
    if (!buffer) return {nullptr, close_input};
    // bd_seek/bd_seek_time snap to access points, so neither can implement an exact AVIO seek.
    *io = avio_alloc_context(buffer, 32768, 0, &slice, read_bluray, nullptr, nullptr);
    if (!*io) {
        av_free(buffer);
        return {nullptr, close_input};
    }
    auto* fmt = avformat_alloc_context();
    if (!fmt) return {nullptr, close_input};
    fmt->pb = *io;
    fmt->flags |= AVFMT_FLAG_CUSTOM_IO | AVFMT_FLAG_NOPARSE | AVFMT_FLAG_NOFILLIN;
    fmt->probesize = 2 * MiB;
    const auto* format = av_find_input_format("mpegts");
    if (!format) {
        avformat_free_context(fmt);
        return {nullptr, close_input};
    }
    if (avformat_open_input(&fmt, nullptr, format, nullptr) < 0) return {nullptr, close_input};
    return {fmt, close_input};
}

bool open_disc(BlurayReader& r, const char* path) {
    struct stat st{};
    if (stat(path, &st) != 0 || !S_ISREG(st.st_mode)) return false;
    r.file.reset(std::fopen(path, "rb"));
    r.disc.reset(bd_init());
    if (!r.file || !r.disc || std::setvbuf(r.file.get(), nullptr, _IONBF, 0) != 0
        || !bd_open_stream(r.disc.get(), &r, read_disc_blocks)) return false;
    const auto* info = bd_get_disc_info(r.disc.get());
    return info && info->bluray_detected && !info->aacs_detected && !info->bdplus_detected && !r.limits.exhausted;
}

int single_playlist(const char* path, Limits& limits) {
    BlurayReader reader{limits};
    if (!open_disc(reader, path)) return 0;
    auto close = [](BD_DIR_H* dir) { dir->close(dir); };
    const std::unique_ptr<BD_DIR_H, decltype(close)> dir(bd_open_dir(reader.disc.get(), "BDMV/PLAYLIST"), close);
    if (!dir) return 0;
    std::string name;
    for (unsigned count = 0; count < 4096; ++count) {
        BD_DIRENT entry{};
        const int result = dir->read(dir.get(), &entry);
        if (limits.exhausted || std::ferror(reader.file.get())) return 0;
        // libbluray 1.3.4's UDF adapter returns -1 at end of this in-memory directory.
        if (result != 0) {
            if (!name.empty()) std::puts(name.c_str());
            return 0;
        }
        const std::string_view file(entry.d_name, strnlen(entry.d_name, sizeof(entry.d_name)));
        if (file.size() < 5 || strncasecmp(file.data() + file.size() - 5, ".mpls", 5) != 0) continue;
        if (!name.empty() || file.size() != 10 || !file.ends_with(".mpls")
            || !std::all_of(file.begin(), file.begin() + 5, [](char c) { return c >= '0' && c <= '9'; })) return 0;
        name.assign(file);
    }
    return 0; // No complete enumeration, no proof.
}

bool open_bluray(BlurayReader& r, const char* path, int playlist, int pid, double& duration) {
    if (!open_disc(r, path)) return false;
    using Title = std::unique_ptr<BLURAY_TITLE_INFO, decltype(&bd_free_title_info)>;
    Title selected(nullptr, bd_free_title_info);
    const auto count = bd_get_titles(r.disc.get(), TITLES_RELEVANT, 180); // Same default selection as FFmpeg.
    for (uint32_t i = 0; i < count && !r.limits.exhausted; ++i) {
        Title candidate(bd_get_title_info(r.disc.get(), i, 0), bd_free_title_info);
        if (!candidate) continue;
        if (playlist >= 0) {
            if (candidate->playlist != static_cast<uint32_t>(playlist)) continue;
            selected = std::move(candidate);
            break;
        }
        if (!selected || candidate->duration > selected->duration) selected = std::move(candidate);
    }
    if (!selected || r.limits.exhausted || !bd_select_playlist(r.disc.get(), selected->playlist)) return false;
    bool has_pid = false;
    uint64_t byte = 0, time = 0;
    r.clips.reserve(selected->clip_count);
    for (uint32_t i = 0; i < selected->clip_count; ++i) {
        const auto& clip = selected->clips[i];
        if (clip.start_time != time || clip.out_time < clip.in_time) return false;
        const auto end_byte = byte + clip.pkt_count * m2ts_packet_bytes;
        const auto end_time = time + clip.out_time - clip.in_time;
        bool clip_has_pid = false;
        for (uint8_t j = 0; j < clip.pg_stream_count; ++j) clip_has_pid |= clip.pg_streams[j].pid == pid;
        has_pid |= clip_has_pid;
        r.clips.push_back({byte, end_byte, time, end_time, clip.in_time, clip_has_pid});
        byte = end_byte;
        time = end_time;
    }
    duration = time / double(bluray_clock);
    return has_pid && byte > 0 && byte == bd_get_title_size(r.disc.get())
        && time > 0 && time == selected->duration;
}

bool bitmap(AVCodecID id) {
    return id == AV_CODEC_ID_HDMV_PGS_SUBTITLE || id == AV_CODEC_ID_DVD_SUBTITLE
        || id == AV_CODEC_ID_DVB_SUBTITLE || id == AV_CODEC_ID_XSUB;
}

using Decoder = std::unique_ptr<AVCodecContext, void(*)(AVCodecContext*)>;
Decoder open_decoder(const AVStream* stream) {
    const auto* codec = avcodec_find_decoder(stream->codecpar->codec_id);
    Decoder context(avcodec_alloc_context3(codec), [](AVCodecContext* p) { avcodec_free_context(&p); });
    if (!codec || !context || avcodec_parameters_to_context(context.get(), stream->codecpar) < 0) {
        context.reset();
        return context;
    }
    context->thread_count = 1;
    context->pkt_timebase = stream->time_base;
    if (avcodec_open2(context.get(), codec, nullptr) < 0) context.reset();
    return context;
}

struct Subtitle {
    AVSubtitle data{};
    ~Subtitle() { avsubtitle_free(&data); }
};

bool init_ocr(tesseract::TessBaseAPI& ocr, const char* model_dir) {
    if (ocr.Init(model_dir, "chi_sim+chi_tra+eng", tesseract::OEM_LSTM_ONLY) != 0) return false;
    ocr.SetPageSegMode(tesseract::PSM_SINGLE_BLOCK);
    return true;
}

void emit_ocr(tesseract::TessBaseAPI& ocr, const uint8_t* pixels, int w, int h,
              Limits& limits, size_t region, size_t& readable) {
    limits.pace();
    ocr.SetImage(pixels, w, h, 1, w);
    ocr.SetSourceResolution(150);
    if (ocr.Recognize(nullptr) == 0) {
        constexpr auto line = tesseract::RIL_TEXTLINE;
        const std::unique_ptr<tesseract::ResultIterator> result(ocr.GetIterator());
        bool first = true;
        if (result && !result->Empty(line)) do {
            const std::unique_ptr<char[]> text(result->GetUTF8Text(line));
            const float confidence = result->Confidence(line);
            if (!text || !text[0] || confidence <= 0) continue;
            std::string_view contents(text.get());
            while (!contents.empty() && (contents.back() == '\r' || contents.back() == '\n')) contents.remove_suffix(1);
            if (contents.empty() || contents.find_first_of("\r\n\f") != std::string_view::npos) continue;
            if (first) {
                std::printf("%zu\n", region);
                first = false;
            }
            std::printf("%d\t%.*s\n", static_cast<int>(confidence), static_cast<int>(contents.size()), contents.data());
        } while (result->Next(line));
        if (!first) {
            std::putchar('\f');
            ++readable;
        }
    }
    ocr.Clear();
    limits.pace();
}

template<size_t N>
bool read_tag(FILE* file, const std::array<uint8_t, N>& tag) {
    std::array<uint8_t, N> actual;
    return std::fread(actual.data(), 1, N, file) == N && actual == tag;
}

int review(const char* path, const char* model_dir, Limits& limits) {
    const int fd = open(path, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC);
    if (fd < 0) return 3;
    const std::unique_ptr<FILE, decltype(&std::fclose)> input(fdopen(fd, "rb"), std::fclose);
    if (!input) { close(fd); return 3; }
    auto* file = input.get();
    struct stat info{};
    constexpr auto framing = static_cast<off_t>(sample_magic.size() + sample_end.size());
    if (fstat(fd, &info) != 0 || !S_ISREG(info.st_mode) || info.st_size < framing || info.st_size > sample_bytes)
        return 3;
    // Refuse an unfinished spool even when the CPU budget won't reach the last record.
    const auto end = info.st_size - static_cast<off_t>(sample_end.size());
    if (fseeko(file, end, SEEK_SET) != 0 || !read_tag(file, sample_end)
        || fseeko(file, 0, SEEK_SET) != 0 || !read_tag(file, sample_magic)) return 3;

    tesseract::TessBaseAPI ocr;
    if (!init_ocr(ocr, model_dir)) return 2;
    size_t remaining = static_cast<size_t>(info.st_size - framing);
    size_t capacity = 0, reviewed = 0, readable = 0;
    std::unique_ptr<uint8_t[]> pixels;
    while (remaining && Limits::cpu_ms() < cpu_budget_ms) {
        SampleHeader header;
        if (remaining < header.size() || std::fread(header.data(), 1, header.size(), file) != header.size()) return 3;
        remaining -= header.size();
        const int w = AV_RL16(header.data()), h = AV_RL16(header.data() + 2);
        const size_t region = header[4];
        if (w < 17 || w > max_width + 16 || h < 17 || h > max_height + 16 || region >= region_count) return 3;
        const size_t count = static_cast<size_t>(w) * h;
        if (count > remaining) return 3;
        if (count > capacity) {
            pixels.reset();
            pixels = std::make_unique_for_overwrite<uint8_t[]>(count);
            capacity = count;
        }
        if (std::fread(pixels.get(), 1, count, file) != count
            || sample_crc(header, pixels.get(), count) != AV_RL32(header.data() + 5)) return 3;
        remaining -= count;
        if (Limits::cpu_ms() >= cpu_budget_ms) break;
        emit_ocr(ocr, pixels.get(), w, h, limits, region, readable);
        ++reviewed;
    }
    std::fprintf(stderr, "bitmap OCR review: %zu images, %zu readable, %lld CPU ms\n",
                 reviewed, readable, static_cast<long long>(Limits::cpu_ms()));
    return 0;
}

bool recognize(tesseract::TessBaseAPI& ocr, const AVSubtitle& sub, std::unordered_set<std::string>& seen,
               Limits& limits, size_t region, size_t& readable, SampleWriter& samples) {
    int x = max_width, y = max_height, right = 0, bottom = 0;
    for (unsigned i = 0; i < sub.num_rects; ++i) {
        const auto& r = *sub.rects[i];
        if (r.type != SUBTITLE_BITMAP || !r.data[0] || !r.data[1] || r.w <= 0 || r.h <= 0 || r.linesize[0] < r.w
            || r.x < 0 || r.y < 0 || r.x > max_width || r.y > max_height || r.w > max_width - r.x || r.h > max_height - r.y) return false;
        x = std::min(x, r.x); y = std::min(y, r.y);
        right = std::max(right, r.x + r.w); bottom = std::max(bottom, r.y + r.h);
    }
    if (right <= x || bottom <= y) return false;
    const int w = right - x + 16, h = bottom - y + 16;
    std::string pixels(static_cast<size_t>(w) * h, '\xff');
    for (unsigned i = 0; i < sub.num_rects; ++i) {
        const auto& r = *sub.rects[i];
        uint8_t gray[256];
        for (int j = 0; j < 256; ++j) {
            uint32_t color = 0;
            if (j < r.nb_colors) std::memcpy(&color, r.data[1] + j * 4, 4);
            unsigned brightness = std::max({color & 255, (color >> 8) & 255, (color >> 16) & 255});
            gray[j] = 255 - ((color >> 24) * brightness / 255);
        }
        for (int row = 0; row < r.h; ++row) {
            auto* dest = pixels.data() + (r.y - y + 8 + row) * w + r.x - x + 8;
            const auto* src = r.data[0] + row * r.linesize[0];
            for (int col = 0; col < r.w; ++col) dest[col] = std::min(static_cast<uint8_t>(dest[col]), gray[src[col]]);
        }
    }
    // Keep only fixed-size fingerprints, not the decoded bitmaps.
    auto key = std::to_string(w) + ":" + std::to_string(h) + ":" + std::to_string(std::hash<std::string>{}(pixels));
    if (!seen.insert(std::move(key)).second) return false;
    const auto* data = reinterpret_cast<const uint8_t*>(pixels.data());
    emit_ocr(ocr, data, w, h, limits, region, readable);
    samples.append(data, w, h, region);
    return true;
}

struct Region {
    double from, probe_end, end;
    double resume = 0;
    double yield = 0;
    size_t images = 0;
};

int scan(AVFormatContext* fmt, AVStream* stream, BlurayReader* bluray, int pid,
         tesseract::TessBaseAPI& ocr, Limits& limits, double duration, bool raw_sup, SampleWriter& samples) {
    AVPacket* packet = av_packet_alloc();
    if (!packet) return 2;
    std::unique_ptr<AVPacket, void(*)(AVPacket*)> holder(packet, [](AVPacket* p) { av_packet_free(&p); });
    const double origin = !fmt || fmt->start_time == AV_NOPTS_VALUE || raw_sup
        ? 0 : fmt->start_time / double(AV_TIME_BASE);
    std::unordered_set<std::string> seen;
    std::array<Region, region_count> regions{};
    size_t attempts = 0, visited = 0, readable = 0;
    size_t subtitle_packets = 0;
    double prior_end = 0;
    for (size_t i = 0; i < regions.size(); ++i) {
        const double center = duration * fractions[i];
        const double from = std::max(prior_end, std::max(0., center - window_seconds / 2));
        const double until = std::min(duration, center + window_seconds / 2);
        const double boundary = i + 1 < regions.size() ? duration * (fractions[i] + fractions[i + 1]) / 2 : duration;
        regions[i] = {from, until, std::max(until, boundary)};
        prior_end = until;
    }

    auto decode = [&](AVCodecContext* decoder, Subtitle& sub) {
        int got = 0;
        return avcodec_decode_subtitle2(decoder, &sub.data, &got, packet) >= 0 && got;
    };

    auto seek_window = [&](size_t region, double from, double until, int64_t cpu_end, bool extend) {
        if (until <= from || limits.total <= 0 || Limits::cpu_ms() >= cpu_end) return from;
        ++attempts;
        limits.remaining = window_bytes;
        limits.exhausted = false;
        double last_new = from, resume = from;
        enum class Result { complete, eof, paused };
        // A fresh decoder prevents PGS cache reuse across seeks; 7.1.3 has no PGS flush callback.
        auto read_window = [&](AVFormatContext* input, AVStream* target, const BlurayClip* clip) {
            auto decoder = open_decoder(target);
            if (!decoder) return Result::paused;
            while (Limits::cpu_ms() < cpu_end && !limits.exhausted) {
                const auto read = av_read_frame(input, packet);
                if (read < 0) return read == AVERROR_EOF && !limits.exhausted ? Result::eof : Result::paused;
                limits.pace();
                const bool selected = packet->stream_index == target->index;
                const auto pts = packet->pts == AV_NOPTS_VALUE ? packet->dts : packet->pts;
                const auto base = input->streams[packet->stream_index]->time_base;
                const auto at = pts == AV_NOPTS_VALUE ? -1. : clip ? clip->time(pts, base) : pts * av_q2d(base) - origin;
                const bool stop = at >= until && (selected || at >= until + 2);
                const bool quiet = extend && at >= last_new + window_seconds
                    && (selected || at >= last_new + window_seconds + 2);
                if (selected && !stop && !quiet && (clip || at < 0 || at >= from - preroll_seconds)) {
                    const bool keep = at >= from && at < until && (!clip ||
                        (at >= clip->start / double(bluray_clock) && at < clip->end / double(bluray_clock)));
                    if (keep) ++subtitle_packets;
                    Subtitle sub;
                    if (decode(decoder.get(), sub) && keep
                        && recognize(ocr, sub.data, seen, limits, region, readable, samples)) {
                        last_new = std::max(last_new, at);
                        ++regions[region].images;
                    }
                    // Resume at the subtitle frontier, not at a later interleaved video PTS.
                    if (keep) resume = std::max(resume, at);
                }
                av_packet_unref(packet);
                if (stop) return Result::complete;
                if (quiet) break;
            }
            return Result::paused;
        };

        const auto seek = std::max(0., from - preroll_seconds);
        if (!bluray) {
            if (av_seek_frame(fmt, -1, static_cast<int64_t>((origin + seek) * AV_TIME_BASE), AVSEEK_FLAG_BACKWARD) < 0)
                return from;
            ++visited;
            const auto result = read_window(fmt, stream, nullptr);
            if (result == Result::eof) return regions[region].end;
            return result == Result::complete ? until : std::min(until, resume);
        }

        // Keep each transport stream demuxer within one play item. A PID can
        // restart its MPEG-TS clock (or even replay the same clip) at a boundary.
        const auto tick = static_cast<uint64_t>(seek * bluray_clock);
        const auto& clips = bluray->clips;
        bool opened = false;
        for (auto it = std::upper_bound(clips.begin(), clips.end(), tick,
                 [](uint64_t t, const BlurayClip& clip) { return t < clip.end; }); it != clips.end(); ++it) {
            if (it->start / double(bluray_clock) >= until || limits.exhausted || Limits::cpu_ms() >= cpu_end) break;
            if (!it->has_pid || it->first_byte == it->end_byte || it->start == it->end) continue;
            const auto disc = bluray->disc.get();
            const auto pos = tick <= it->start
                ? bd_seek_playitem(disc, static_cast<unsigned>(it - clips.begin())) : bd_seek_time(disc, tick);
            if (pos < 0 || static_cast<uint64_t>(pos) < it->first_byte || static_cast<uint64_t>(pos) >= it->end_byte) continue;
            BluraySlice slice{*bluray, it->end_byte};
            std::unique_ptr<AVIOContext, decltype(&close_bluray_io)> input_io(nullptr, close_bluray_io);
            AVIOContext* io = nullptr;
            auto input = open_bluray_input(slice, &io);
            input_io.reset(io);
            if (!input || limits.exhausted) continue;
            AVStream* target = nullptr;
            for (unsigned i = 0; i < input->nb_streams; ++i) {
                auto* candidate = input->streams[i];
                if (candidate->id != pid || candidate->codecpar->codec_id != AV_CODEC_ID_HDMV_PGS_SUBTITLE) continue;
                if (target) return from; // Refuse ambiguous PGS stream identity.
                target = candidate;
            }
            if (!target) continue;
            if (!opened) {
                ++visited;
                opened = true;
            }
            const auto result = read_window(input.get(), target, &*it);
            if (result == Result::complete) return until;
            if (result == Result::paused) break;
            resume = std::max(resume, it->end / double(bluray_clock));
        }
        return std::min(until, resume);
    };

    if (raw_sup) {
        // .sup has no seek index. Feed every segment to the decoder in this bounded
        // sidecar pass: display sets can reuse objects and palettes long after they were defined.
        auto decoder = open_decoder(stream);
        if (!decoder) return 3;
        ++attempts;
        ++visited;
        while (Limits::cpu_ms() < cpu_budget_ms && av_read_frame(fmt, packet) >= 0) {
            Subtitle sub;
            const bool display = decode(decoder.get(), sub);
            av_packet_unref(packet);
            if (!display || sub.data.pts == AV_NOPTS_VALUE) continue;
            const double at = sub.data.pts / double(AV_TIME_BASE);
            if (at >= regions.back().probe_end) break;
            for (size_t i = 0; i < regions.size(); ++i) {
                const auto& r = regions[i];
                if (at < r.from || at >= r.probe_end) continue;
                ++subtitle_packets;
                if (recognize(ocr, sub.data, seen, limits, i, readable, samples)) ++regions[i].images;
                break;
            }
        }
    } else {
        // First cover the whole timeline. Reserve half the CPU time for search/continuations.
        const auto probe_cpu = cpu_budget_ms / 2;
        for (size_t i = 0; i < regions.size(); ++i) {
            auto& r = regions[i];
            const auto before = limits.total;
            const auto now = Limits::cpu_ms();
            const auto cpu_end = now + std::max<int64_t>(0, probe_cpu - now) / static_cast<int64_t>(regions.size() - i);
            r.resume = seek_window(i, r.from, r.probe_end, cpu_end, false);
            r.yield = static_cast<double>(r.images) / std::max<int64_t>(32768, before - limits.total);
        }
        std::array<size_t, region_count> order{};
        for (size_t i = 0; i < order.size(); ++i) order[i] = i;
        std::stable_sort(order.begin(), order.end(), [&](size_t a, size_t b) { return regions[a].yield > regions[b].yield; });
        for (const auto i : order) {
            const auto& r = regions[i];
            if (r.from >= r.probe_end) continue;
            if (r.images) {
                seek_window(i, r.resume, r.end, cpu_budget_ms, true);
            } else if (r.probe_end < r.end) {
                // A quiet first probe is not representative; try the unsampled part once.
                const double start = (r.probe_end + r.end) / 2;
                seek_window(i, start, r.end, cpu_budget_ms, true);
            }
        }
    }
    size_t productive = 0;
    for (const auto& r : regions) productive += r.images > 0;
    std::fprintf(stderr, "bitmap OCR: %zu images, %zu readable, %zu productive regions, %zu subtitle packets in windows, %zu/%zu windows, %lld bytes, %lld CPU ms\n",
                 seen.size(), readable, productive, subtitle_packets, visited, attempts, static_cast<long long>(track_bytes - limits.total),
                 static_cast<long long>(Limits::cpu_ms()));
    return visited ? 0 : 3;
}
} // namespace

int main(int argc, char** argv) {
    const bool playlist_mode = argc == 3 && std::strcmp(argv[1], "--single-playlist") == 0;
    const bool review_mode = argc == 4 && std::strcmp(argv[1], "--review") == 0;
    const bool bluray_mode = argc > 1 && std::strcmp(argv[1], "--bluray") == 0;
    if (!playlist_mode && !review_mode && (bluray_mode ? argc != 7 && argc != 8 : argc != 5 && argc != 6)) return 2;
    rlimit cpu{18, 18};
    if (setrlimit(RLIMIT_CPU, &cpu) != 0) return 2;
#ifdef __linux__
    rlimit memory{768 * MiB, 768 * MiB};
    if (setrlimit(RLIMIT_AS, &memory) != 0) return 2;
#endif
    av_log_set_level(AV_LOG_FATAL);
    av_max_alloc(32 * MiB);
    Limits limits;
    if (playlist_mode) return single_playlist(argv[2], limits);
    if (review_mode) return review(argv[2], argv[3], limits);
    limits.main = argv[bluray_mode ? 2 : 1];
    const bool vobsub = !bluray_mode && (limits.main.ends_with(".idx") || limits.main.ends_with(".IDX"));
    if (vobsub) limits.companion = limits.main.substr(0, limits.main.size() - 4)
        + (limits.main.ends_with(".IDX") ? ".SUB" : ".sub");
    const auto index = std::atoi(argv[bluray_mode ? 4 : 2]);
    double duration = std::atof(argv[bluray_mode ? 5 : 3]);
    if (!std::isfinite(duration) || (!bluray_mode && duration <= 0)) return 2;
    BlurayReader bluray{limits};
    if (bluray_mode && (index <= 0 || index >= 8192
        || !open_bluray(bluray, limits.main.c_str(), std::atoi(argv[3]), index, duration))) {
        std::fprintf(stderr, "Blu-ray ISO is unreadable, encrypted, or does not contain the requested subtitle PID\n");
        return 3;
    }
    if (bluray_mode) {
        tesseract::TessBaseAPI ocr;
        if (!init_ocr(ocr, argv[6])) return 2;
        SampleWriter samples(argc == 8 ? argv[7] : nullptr);
        const int status = scan(nullptr, nullptr, &bluray, index, ocr, limits, duration, false, samples);
        if (status == 0) samples.finish();
        return status;
    }
    AVIOContext* io = nullptr;
    if (open_io(limits, &io, limits.main.c_str()) < 0) return 3;
    const std::unique_ptr<AVIOContext, decltype(&close_io)> input_io(io, close_io);
    AVFormatContext* raw = avformat_alloc_context();
    if (!raw) return 2;
    AVDictionary* options = nullptr;
    av_dict_set(&options, "format_whitelist", vobsub ? "vobsub,mpeg" : "matroska,mov,avi,mpegts,mpeg,sup", 0);
    if (vobsub) {
        // Fail closed with an unpatched FFmpeg rather than permit a direct .sub open.
        av_dict_set(&options, "protocol_blacklist", "file", 0);
        raw->opaque = &limits;
        raw->io_open = open_companion;
        raw->io_close2 = close_companion;
    }
    raw->pb = io;
    raw->flags |= AVFMT_FLAG_CUSTOM_IO;
    raw->probesize = 2 * MiB;
    raw->max_analyze_duration = 2 * AV_TIME_BASE;
    raw->skip_estimate_duration_from_pts = 1; // Use Jellyfin's duration; don't probe the end of a TS.
    const auto* format = vobsub ? av_find_input_format("vobsub") : nullptr;
    const int opened = avformat_open_input(&raw, limits.main.c_str(), format, &options);
    av_dict_free(&options);
    if (opened < 0) return 3;
    Input fmt(raw, close_input);
    if (!fmt->pb || !(fmt->pb->seekable & AVIO_SEEKABLE_NORMAL)) return 3;
    // Broadcast timestamps can start hours into a clock. Discover the local timeline before sampling.
    const bool transport_stream = std::strcmp(fmt->iformat->name, "mpegts") == 0;
    if (transport_stream) avformat_find_stream_info(fmt.get(), nullptr);
    AVStream* stream = nullptr;
    for (unsigned attempt = 0; attempt < 2 && !stream; ++attempt) {
        if (attempt && !transport_stream) avformat_find_stream_info(fmt.get(), nullptr);
        for (unsigned i = 0; i < fmt->nb_streams; ++i)
            if ((index < 0 || static_cast<unsigned>(index) == i) && bitmap(fmt->streams[i]->codecpar->codec_id)) {
                if (stream) return 3; // A sidecar index may not match Jellyfin's global track index.
                stream = fmt->streams[i];
            }
    }
    if (!stream || limits.exhausted) return 3;
    if (!avcodec_find_decoder(stream->codecpar->codec_id)) return 3;
    tesseract::TessBaseAPI ocr;
    if (!init_ocr(ocr, argv[4])) return 2;
    SampleWriter samples(argc == 6 ? argv[5] : nullptr);
    const int status = scan(fmt.get(), stream, nullptr, 0, ocr, limits, duration,
                            std::strcmp(fmt->iformat->name, "sup") == 0, samples);
    if (status == 0) samples.finish();
    return status;
}
