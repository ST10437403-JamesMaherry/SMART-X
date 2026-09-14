import { Navigate, Route, Routes } from 'react-router-dom';
import GatewayHomePage from './pages/GatewayHomePage';
import IngestionPage from './pages/IngestionPage';
import { routes } from './routing/routes';
import './App.css';

/**
 * Declares the top-level Smart-X client routes.
 *
 * Future architectural pillars are deliberately not routable during Part 1.
 * They will be introduced only when their corresponding PoE phases are
 * implemented.
 */
function App() {
  return (
    <Routes>
      <Route path={routes.home} element={<GatewayHomePage />} />

      <Route path={routes.ingestion} element={<IngestionPage />} />

      {/* Unknown client URLs recover safely instead of rendering a broken UI. */}
      <Route path="*" element={<Navigate to={routes.home} replace />} />
    </Routes>
  );
}

export default App;
