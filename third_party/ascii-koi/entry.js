// PhoneBox entry: pond.js is an IIFE that publishes window.AsciiKoi; this re-exports the engine.
import './pond.js';
const { createPond } = globalThis.AsciiKoi;
export { createPond };
