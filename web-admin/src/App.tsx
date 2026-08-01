import { Routes, Route } from "react-router-dom";
import Layout from "./components/Layout";
import Dashboard from "./pages/Dashboard";
import Students from "./pages/Students";
import StudentsImport from "./pages/StudentsImport";
import Events from "./pages/Events";
import EventDetail from "./pages/EventDetail";
import Devices from "./pages/Devices";

export default function App() {
  return (
    <Layout>
      <Routes>
        <Route path="/" element={<Dashboard />} />
        <Route path="/students" element={<Students />} />
        {/* Declared before nothing and after `/students` only for readability — the paths are
            distinct, so order does not decide the match. No nav entry of its own: `Layout`'s
            `isActive` matches on `startsWith`, so Students stays highlighted while the import runs,
            which is where the operator came from and where they go back to. */}
        <Route path="/students/import" element={<StudentsImport />} />
        <Route path="/events" element={<Events />} />
        <Route path="/events/:id" element={<EventDetail />} />
        <Route path="/devices" element={<Devices />} />
      </Routes>
    </Layout>
  );
}
