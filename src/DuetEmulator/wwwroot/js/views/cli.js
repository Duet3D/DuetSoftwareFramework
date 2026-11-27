// What the page does, as the scripts/emulate.py command that does the same

/** The command and the port option it needs to reach this server */
export function cliPrefix() {
  const port = location.port || '80';
  return port === '8770' ? 'scripts/emulate.py' : `scripts/emulate.py --port ${port}`;
}

/** A word quoted for a POSIX shell when it needs it */
export function quote(text) {
  return /^[A-Za-z0-9_./=:@%+-]+$/.test(text) ? text : `'${text.replaceAll("'", "'\\''")}'`;
}

/** The command that runs a step: send for a code, the input commands where there is one, step for the rest */
export function stepCommand(step, yaml) {
  const prefix = cliPrefix();
  if (step.code !== undefined) return `${prefix} send ${quote(step.code)}${step.timeout ? ` --timeout ${step.timeout}` : ''}`;
  if (step.wait !== undefined) return `${prefix} wait ${step.wait}`;
  if (step.note !== undefined) return `${prefix} note ${quote(step.note)}`;
  if (step.drive && !step.drive.afterSteps) return `${prefix} drive ${step.drive.board} ${step.drive.port} ${step.drive.level}`;
  if (step.release) return `${prefix} release ${step.release.board} ${step.release.port}`;
  if (step.thermistor) return `${prefix} thermistor ${step.thermistor.board} ${step.thermistor.port} ${step.thermistor.celsius}`;
  if (step.analog) return `${prefix} analog ${step.analog.board} ${step.analog.port} ${step.analog.value}`;
  return `${prefix} step ${quote(yaml)}`;
}

/** A step as a scenario writes it on one line, such as drive: {board: 1, port: io0.in, level: high} */
export function stepYaml(step) {
  return Object.entries(step).filter(([, v]) => v !== undefined && v !== null).map(([k, v]) => `${k}: ${yamlValue(v)}`).join('\n');
}

function yamlValue(value) {
  if (value !== null && typeof value === 'object') {
    return `{${Object.entries(value).filter(([, v]) => v !== undefined && v !== null).map(([k, v]) => `${k}: ${yamlValue(v)}`).join(', ')}}`;
  }
  if (typeof value !== 'string') return String(value);
  // Quoted where YAML would read it as something else: a number, a boolean, null, or text with characters that mean something
  const plain = value.length > 0 && !/^[\s\-?:,[\]{}#&*!|>'"%@`]|[\s]$|: | #|[{}[\],]/.test(value)
    && !/^(true|false|yes|no|on|off|null|~)$/i.test(value) && !/^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$/.test(value);
  return plain ? value : JSON.stringify(value);
}
