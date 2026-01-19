using System;
using System.Collections.Generic;
using UnityEngine;

namespace AI.BehaviorTree
{
    public enum NodeState { Success, Failure, Running }

    public abstract class BTNode
    {
        public abstract NodeState Tick();
    }

    // Composite: runs children in order until one succeeds (Selector) or fails (Sequence)
    public class Selector : BTNode
    {
        private readonly List<BTNode> children = new List<BTNode>();
        public Selector(params BTNode[] nodes) { children.AddRange(nodes); }
        public override NodeState Tick()
        {
            foreach (var c in children)
            {
                var s = c.Tick();
                if (s == NodeState.Success) return NodeState.Success;
                if (s == NodeState.Running) return NodeState.Running;
            }
            return NodeState.Failure;
        }
    }

    public class Sequence : BTNode
    {
        private readonly List<BTNode> children = new List<BTNode>();
        public Sequence(params BTNode[] nodes) { children.AddRange(nodes); }
        public override NodeState Tick()
        {
            foreach (var c in children)
            {
                var s = c.Tick();
                if (s == NodeState.Failure) return NodeState.Failure;
                if (s == NodeState.Running) return NodeState.Running;
            }
            return NodeState.Success;
        }
    }

    // Condition node wraps a predicate
    public class ConditionNode : BTNode
    {
        private readonly Func<bool> predicate;
        public ConditionNode(Func<bool> pred) { predicate = pred; }
        public override NodeState Tick() => predicate() ? NodeState.Success : NodeState.Failure;
    }

    // Action node wraps an action returning NodeState
    public class ActionNode : BTNode
    {
        private readonly Func<NodeState> action;
        public ActionNode(Func<NodeState> a) { action = a; }
        public override NodeState Tick() => action == null ? NodeState.Failure : action();
    }

    // Simple behavior tree container
    public class BehaviorTree
    {
        private readonly BTNode root;
        public BehaviorTree(BTNode rootNode) { root = rootNode; }
        public NodeState Tick()
        {
            if (root == null) return NodeState.Failure;
            return root.Tick();
        }
    }
}