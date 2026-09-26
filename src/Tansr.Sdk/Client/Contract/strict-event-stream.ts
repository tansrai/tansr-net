/** Package-internal staged SDK2 decoder. Existing SDK1 SseParser intentionally remains unchanged. */
import { copyEventContext, decodeSdk2EventFrame, SDK2_EVENT_SSE_BYTES, Sdk2StreamDecodeError, streamFail,
  type Sdk2EventContext, type Sdk2EventFrame } from './event-frame.js';

// Four line terminators per canonical data frame. Only verified CRLF discounts one CR.
const RAW_BYTES = SDK2_EVENT_SSE_BYTES + 4;
const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });

/**
 * One bounded raw frame buffer; arbitrary UTF-8/network splits do not cause rescans or concatenation.
 * 完整帧逐个交付并等待消费完成；不累计交付队列，不写游标或 ACK。
 * 宿主决定业务消费、持久游标与网络重连；消费失败后本解析器永久关闭。
 */
export class StrictSdk2EventStream {
  readonly #context: Sdk2EventContext;
  readonly #raw = new Uint8Array(RAW_BYTES);
  #used = 0;
  #normalized = 0;
  #lineStart = 0;
  #crCount = 0;
  #pendingCr = false;
  #id: string | null = null;
  #event: string | null = null;
  #data: string | null = null;
  #comments = false;
  #busy = false;
  #closed = false;
  #failed = false;

  constructor(context: Sdk2EventContext) { this.#context = copyEventContext(context); }

  /** Complete frames are delivered in order; the decoder never stores or advances a cursor. */
  async feed(chunk: Uint8Array, deliver: (frame: Sdk2EventFrame) => void | Promise<void>): Promise<void> {
    this.#enter();
    try {
      if (!(chunk instanceof Uint8Array) || typeof deliver !== 'function') streamFail('invalid_frame');
      for (let offset = 0; offset < chunk.byteLength; offset++) {
        const byte = chunk[offset]!;
        if (this.#used >= RAW_BYTES) streamFail('frame_too_large');
        this.#raw[this.#used++] = byte;
        const crlf = this.#pendingCr;
        if (crlf && byte !== 10) streamFail('invalid_encoding');
        this.#pendingCr = false;
        if (byte === 13) {
          if (++this.#crCount > 4) streamFail('invalid_frame');
          this.#pendingCr = true;
          continue;
        }
        if (++this.#normalized > SDK2_EVENT_SSE_BYTES) streamFail('frame_too_large');
        if (byte !== 10) continue;
        const end = this.#used - (crlf ? 2 : 1);
        let line: string;
        try { line = decoder.decode(this.#raw.subarray(this.#lineStart, end)); }
        catch { streamFail('invalid_encoding'); }
        this.#lineStart = this.#used;
        if (line === '') {
          const frame = this.#complete();
          this.#resetFrame();
          if (frame) {
            await deliver(frame);
            if (this.#failed) streamFail('reentrant');
          }
        } else this.#line(line);
      }
    } catch (error) { this.#failed = true; throw error; }
    finally { this.#busy = false; }
  }

  /** EOF never dispatches a partial frame, including a valid JSON line lacking its blank terminator. */
  finish(): void {
    this.#enter();
    try {
      if (this.#used !== 0 || this.#pendingCr) streamFail('incomplete_frame');
      this.#closed = true;
    } catch (error) { this.#failed = true; throw error; }
    finally { this.#busy = false; }
  }

  #enter(): void {
    if (this.#busy) { this.#failed = true; streamFail('reentrant'); }
    if (this.#closed || this.#failed) streamFail('closed');
    this.#busy = true;
  }
  #line(line: string): void {
    if (line.startsWith(':')) {
      if (this.#id !== null || this.#event !== null || this.#data !== null) streamFail('invalid_frame');
      this.#comments = true; return;
    }
    if (this.#comments) streamFail('invalid_frame');
    if (this.#id === null && this.#event === null && this.#data === null && line.startsWith('id: ')) this.#id = line.slice(4);
    else if (this.#id !== null && this.#event === null && this.#data === null && line.startsWith('event: ')) this.#event = line.slice(7);
    else if (this.#id !== null && this.#event !== null && this.#data === null && line.startsWith('data: ')) this.#data = line.slice(6);
    else streamFail('invalid_frame'); // Unknown fields, duplicate id/event and multiline data never gain cursor semantics.
  }
  #complete(): Sdk2EventFrame | null {
    if (this.#id === null && this.#event === null && this.#data === null) return null;
    if (this.#id === null || this.#event === null || this.#data === null) streamFail('invalid_frame');
    return decodeSdk2EventFrame(this.#data, this.#id, this.#event, this.#context);
  }
  #resetFrame(): void {
    this.#used = 0; this.#normalized = 0; this.#lineStart = 0; this.#crCount = 0; this.#pendingCr = false;
    this.#id = null; this.#event = null; this.#data = null; this.#comments = false;
  }
}
export { Sdk2StreamDecodeError };
