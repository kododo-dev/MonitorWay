import { defineWay } from '@kododo-dev/way-shell';
import App from './App';
import { monitorWayTheme } from './theme';

// The module Way.Shell loads (built by `npm run build` into dist/way.js).
const way = defineWay({ App, theme: monitorWayTheme });
export default way;
