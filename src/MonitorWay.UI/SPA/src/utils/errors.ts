/** The type's name without its namespace: `System.TimeoutException` as `TimeoutException`. */
export function shortType(type: string): string {
  const generic = type.indexOf('`');
  const name = generic < 0 ? type : type.slice(0, generic);
  const dot = name.lastIndexOf('.');
  return dot < 0 ? type : type.slice(dot + 1);
}

/**
 * Whether a line of a stack trace is a frame of the application, which the error view shows stronger than the frames
 * of the framework.
 */
export function isApplicationFrame(line: string, frames: string[]): boolean {
  const trimmed = line.trim();
  return frames.some(frame => {
    const method = frame.slice(frame.lastIndexOf('.') + 1);
    const type = frame.slice(0, frame.lastIndexOf('.'));
    return trimmed.includes(type) && trimmed.includes(method);
  });
}
