﻿using Sla.EXTRA;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading.Tasks;

namespace Sla.DECCORE
{
    /// \brief All paths from a (putative) switch variable to the CPUI_BRANCHIND
    ///
    /// This is a container for intersecting paths during the construction of a
    /// JumpModel.  It contains every PcodeOp from some starting Varnode through
    /// all paths to a specific BRANCHIND.  The paths can split and rejoin. This also
    /// keeps track of Varnodes that are present on \e all paths, as these are the
    /// potential switch variables for the model.
    internal class PathMeld
    {
        /// \brief A PcodeOp in the path set associated with the last Varnode in the intersection
        ///
        /// This links a PcodeOp to the point where the flow path to it split from common path
        internal struct RootedOp
        {
            /// An op in the container
            internal PcodeOp op;
            /// The index, within commonVn, of the Varnode at the split point
            internal int4 rootVn;
            
            internal RootedOp(PcodeOp o, int4 root)
            {
                op = o;
                rootVn = root;
            }
        }

        /// Varnodes in common with all paths
        private List<Varnode> commonVn;
        /// All the ops for the melded paths
        private List<RootedOp> opMeld;

        /// \brief Calculate intersection of a new Varnode path with the old path
        ///
        /// The new path of Varnodes must all be \e marked. The old path, commonVn,
        /// is replaced with the intersection.  A map is created from the index of each
        /// Varnode in the old path with its index in the new path.  If the Varnode is
        /// not in the intersection, its index is mapped to -1.
        /// \param parentMap will hold the new index map
        private void internalIntersect(List<int4> parentMap)
        {
            List<Varnode*> newVn;
            int4 lastIntersect = -1;
            for (int4 i = 0; i < commonVn.size(); ++i)
            {
                Varnode* vn = commonVn[i];
                if (vn.isMark())
                {       // Look for previously marked varnode, so we know it is in both lists
                    lastIntersect = newVn.size();
                    parentMap.push_back(lastIntersect);
                    newVn.push_back(vn);
                    vn.clearMark();
                }
                else
                    parentMap.push_back(-1);
            }
            commonVn = newVn;
            lastIntersect = -1;
            for (int4 i = parentMap.size() - 1; i >= 0; --i)
            {
                int4 val = parentMap[i];
                if (val == -1)          // Fill in varnodes that are cut out of intersection
                    parentMap[i] = lastIntersect;   // with next earliest varnode that is in intersection
                else
                    lastIntersect = val;
            }
        }

        /// \brief Meld in PcodeOps from a new path into \b this container
        ///
        /// Execution order of the PcodeOps in the container is maintained.  Each PcodeOp, old or new,
        /// has its split point from the common path recalculated.
        /// PcodeOps that split (use a vn not in intersection) and do not rejoin
        /// (have a predecessor Varnode in the intersection) get removed.
        /// If splitting PcodeOps can't be ordered with the existing meld, we get a new cut point.
        /// \param path is the new path of PcodeOps in sequence
        /// \param cutOff is the number of PcodeOps with an input in the common path
        /// \param parentMap is the map from old common Varnodes to the new common Varnodes
        /// \return the index of the last (earliest) Varnode in the common path or -1
        private int4 meldOps(List<PcodeOpNode> path,int4 cutOff, List<int4> parentMap)
        {
            // First update opMeld.rootVn with new intersection information
            for (int4 i = 0; i < opMeld.size(); ++i)
            {
                int4 pos = parentMap[opMeld[i].rootVn];
                if (pos == -1)
                {
                    opMeld[i].op = (PcodeOp*)0;     // Op split but did not rejoin
                }
                else
                    opMeld[i].rootVn = pos;         // New index
            }

            // Do a merge sort, keeping ops in execution order
            List<RootedOp> newMeld;
            int4 curRoot = -1;
            int4 meldPos = 0;               // Ops moved from old opMeld into newMeld
            BlockBasic lastBlock = (BlockBasic*)0;
            for (int4 i = 0; i < cutOff; ++i)
            {
                PcodeOp* op = path[i].op;           // Current op in the new path
                PcodeOp* curOp = (PcodeOp*)0;
                while (meldPos < opMeld.size())
                {
                    PcodeOp* trialOp = opMeld[meldPos].op;  // Current op in the old opMeld
                    if (trialOp == (PcodeOp*)0)
                    {
                        meldPos += 1;
                        continue;
                    }
                    if (trialOp.getParent() != op.getParent())
                    {
                        if (op.getParent() == lastBlock)
                        {
                            curOp = (PcodeOp*)0;        // op comes AFTER trialOp
                            break;
                        }
                        else if (trialOp.getParent() != lastBlock)
                        {
                            // Both trialOp and op come from different blocks that are not the lastBlock
                            int4 res = opMeld[meldPos].rootVn;      // Force truncatePath at (and above) this op

                            // Found a new cut point
                            opMeld = newMeld;               // Take what we've melded so far
                            return res;                 // return the new cutpoint
                        }
                    }
                    else if (trialOp.getSeqNum().getOrder() <= op.getSeqNum().getOrder())
                    {
                        curOp = trialOp;        // op is equal to or comes later than trialOp
                        break;
                    }
                    lastBlock = trialOp.getParent();
                    newMeld.push_back(opMeld[meldPos]); // Current old op moved into newMeld
                    curRoot = opMeld[meldPos].rootVn;
                    meldPos += 1;
                }
                if (curOp == op)
                {
                    newMeld.push_back(opMeld[meldPos]);
                    curRoot = opMeld[meldPos].rootVn;
                    meldPos += 1;
                }
                else
                {
                    newMeld.push_back(RootedOp(op, curRoot));
                }
                lastBlock = op.getParent();
            }
            opMeld = newMeld;
            return -1;
        }

        /// \brief Truncate all paths at the given new Varnode
        ///
        /// The given Varnode is provided as an index into the current common Varnode list.
        /// All Varnodes and PcodeOps involved in execution before this new cut point are removed.
        /// \param cutPoint is the given new Varnode
        private void truncatePaths(int4 cutPoint)
        {
            while (opMeld.size() > 1)
            {
                if (opMeld.back().rootVn < cutPoint)    // If we see op using varnode earlier than cut point
                    break;                  // Keep that and all subsequent ops
                opMeld.pop_back();              // Otherwise cut the op
            }
            commonVn.resize(cutPoint);          // Since intersection is ordered, just resize to cutPoint
        }

        /// Copy paths from another container
        /// \param op2 is the path container to copy from
        public void set(PathMeld op2)
        {
            commonVn = op2.commonVn;
            opMeld = op2.opMeld;
        }

        /// Initialize \b this to be a single path
        /// This container is initialized to hold a single data-flow path.
        /// \param path is the list of PcodeOpNode edges in the path (in reverse execution order)
        public void set(List<PcodeOpNode> path)
        {
            for (int4 i = 0; i < path.size(); ++i)
            {
                PcodeOpNode node = path[i];
                Varnode* vn = node.op.getIn(node.slot);
                opMeld.push_back(RootedOp(node.op, i));
                commonVn.push_back(vn);
            }
        }

        /// Initialize \b this container to a single node "path"
        /// \param op is the one PcodeOp in the path
        /// \param vn is the one Varnode (input to the PcodeOp) in the path
        public void set(PcodeOp op, Varnode vn)
        {
            commonVn.push_back(vn);
            opMeld.push_back(RootedOp(op, 0));
        }

        /// Append a new set of paths to \b this set of paths
        /// The new paths must all start at the common end-point of the paths in
        /// \b this container.  The new set of melded paths start at the original common start
        /// point for \b this container, flow through this old common end-point, and end at
        /// the new common end-point.
        /// \param op2 is the set of paths to be appended
        public void append(PathMeld op2)
        {
            commonVn.insert(commonVn.begin(), op2.commonVn.begin(), op2.commonVn.end());
            opMeld.insert(opMeld.begin(), op2.opMeld.begin(), op2.opMeld.end());
            // Renumber all the rootVn refs to varnodes we have moved
            for (int4 i = op2.opMeld.size(); i < opMeld.size(); ++i)
                opMeld[i].rootVn += op2.commonVn.size();
        }

        /// Clear \b this to be an empty container
        public void clear()
        {
            commonVn.clear();
            opMeld.clear();
        }

        /// Meld a new path into \b this container
        /// Add the new path, recalculating the set of Varnodes common to all paths.
        /// Paths are trimmed to ensure that any path that splits from the common intersection
        /// must eventually rejoin.
        /// \param path is the new path of PcodeOpNode edges to meld, in reverse execution order
        public void meld(List<PcodeOpNode> path)
        {
            List<int4> parentMap;

            for (int4 i = 0; i < path.size(); ++i)
            {
                PcodeOpNode & node(path[i]);
                node.op.getIn(node.slot).setMark();   // Mark varnodes in the new path, so its easy to see intersection
            }
            internalIntersect(parentMap);   // Calculate varnode intersection, and map from old intersection . new
            int4 cutOff = -1;

            // Calculate where the cutoff point is in the new path
            for (int4 i = 0; i < path.size(); ++i)
            {
                PcodeOpNode & node(path[i]);
                Varnode* vn = node.op.getIn(node.slot);
                if (!vn.isMark())
                {   // If mark already cleared, we know it is in intersection
                    cutOff = i + 1;     // Cut-off must at least be past this -vn-
                }
                else
                    vn.clearMark();
            }
            int4 newCutoff = meldOps(path, cutOff, parentMap);  // Given cutoff point, meld in new ops
            if (newCutoff >= 0)                 // If not all ops could be ordered
                truncatePaths(newCutoff);               // Cut off at the point where we couldn't order
            path.resize(cutOff);
        }

        /// Mark PcodeOps paths from the given start
        /// The starting Varnode, common to all paths, is provided as an index.
        /// All PcodeOps up to the final BRANCHIND are (un)marked.
        /// \param val is \b true for marking, \b false for unmarking
        /// \param startVarnode is the index of the starting PcodeOp
        public void markPaths(bool val, int4 startVarnode)
        {
            int4 startOp;
            for (startOp = opMeld.size() - 1; startOp >= 0; --startOp)
            {
                if (opMeld[startOp].rootVn == startVarnode)
                    break;
            }
            if (startOp < 0) return;
            if (val)
            {
                for (int4 i = 0; i <= startOp; ++i)
                    opMeld[i].op.setMark();
            }
            else
            {
                for (int4 i = 0; i <= startOp; ++i)
                    opMeld[i].op.clearMark();
            }
        }

        /// Return the number of Varnodes common to all paths
        public int4 numCommonVarnode() => commonVn.size();

        /// Return the number of PcodeOps across all paths
        public int4 numOps() => opMeld.size();

        /// Get the i-th common Varnode
        public Varnode getVarnode(int4 i) => commonVn[i];

        /// Get the split-point for the i-th PcodeOp
        public Varnode getOpParent(int4 i) => commonVn[opMeld[i].rootVn];

        /// Get the i-th PcodeOp
        public PcodeOp getOp(int4 i) => opMeld[i].op;

        /// Find \e earliest PcodeOp that has a specific common Varnode as input
        /// The Varnode is specified by an index into sequence of Varnodes common to all paths in \b this PathMeld.
        /// We find the earliest (as in executed first) PcodeOp, within \b this PathMeld that uses the Varnode as input.
        /// \param pos is the index of the Varnode
        /// \return the earliest PcodeOp using the Varnode
        public PcodeOp getEarliestOp(int4 pos)
        {
            for (int4 i = opMeld.size() - 1; i >= 0; --i)
            {
                if (opMeld[i].rootVn == pos)
                    return opMeld[i].op;
            }
            return (PcodeOp*)0;
        }

        /// Return \b true if \b this container holds no paths
        public bool empty() => commonVn.empty();
    }
}
